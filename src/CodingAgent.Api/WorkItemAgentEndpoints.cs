using System.Security.Claims;
using System.Text.Json;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints for agent-facing Work Item routes:
/// <c>GET /{id}/assignment</c> and <c>POST /{id}/status</c>.
///
/// These are the only two routes that accept agent-derived keys. Both bind the caller to
/// the work item it was dispatched for; every other route is control-plane and lives in
/// <see cref="WorkItemDispatchEndpoints"/> or <see cref="WorkItemQueryEndpoints"/>.
/// </summary>
public static class WorkItemAgentEndpoints
{
    /// <summary>
    /// Maps agent-facing work item endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-items")
            .RequireAuthorization(ApiAuthPolicies.Agent);

        // ── Agent-facing endpoints ─────────────────────────────────────────
        // These are the only two an agent pod calls (WorkItemHttpClient), and the only two
        // that accept an agent-derived key. Both bind the caller to the work item: an agent
        // may only read the assignment for, and report status on, the item it was dispatched
        // for. The assignment payload carries repository tokens and project secrets, so an
        // unbound agent key would be a cross-tenant read of every credential in the system.
        group.MapGet("/{id:guid}/assignment",
            async (Guid id,
                   [FromServices] IDbContextFactory<PipelineDbContext> dbFactory,
                   [FromServices] IProjectStore projectStore,
                   [FromServices] AssignmentEnricher? assignmentEnricher,
                   HttpContext httpContext,
                   CancellationToken ct) =>
                await AuthorizeAgentForWorkItemAsync(httpContext, id, dbFactory, ct)
                    ?? await GetAssignment(id, dbFactory, projectStore, assignmentEnricher, ct));

        group.MapPost("/{id:guid}/status",
            async (Guid id, [FromBody] WorkItemStatusRequest request,
                   [FromServices] WorkItemTransitionService transitionService,
                   [FromServices] IOrchestratorRunService runService,
                   [FromServices] IRunLifecycleManager runLifecycleManager,
                   [FromServices] IDbContextFactory<PipelineDbContext> dbFactory,
                   HttpContext httpContext,
                   CancellationToken ct) =>
                await AuthorizeAgentForWorkItemAsync(httpContext, id, dbFactory, ct)
                    ?? await PostStatus(id, request, transitionService, runService, runLifecycleManager, dbFactory, ct))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1_048_576)); // 1 MB limit
    }

    // ── Agent → work item binding ─────────────────────────────────────────

    /// <summary>
    /// Confirms that an agent-authenticated caller owns the work item it is addressing.
    ///
    /// Operator-authenticated callers (master key, no <c>agentId</c> query parameter) are the
    /// control plane and pass through untouched. For an agent, the caller's identity — the
    /// <see cref="ClaimTypes.NameIdentifier"/> claim, which <c>AgentApiKeyAuthHandler</c> sets to
    /// the <c>agentId</c> the derived key was issued for — must match the work item's
    /// <c>AssignedAgentId</c> (set at claim time by the Job Controller) or its
    /// <c>K8sJobName</c> (set by <c>DispatchLifecycleService</c>). Both hold the K8s Job name,
    /// which is what the pod reports as its <c>AGENT_ID</c>.
    ///
    /// Fail-closed: an agent addressing a work item with neither field set is refused. A
    /// missing work item returns <see langword="null"/> so the handler produces its own 404
    /// rather than leaking existence through the status code.
    /// </summary>
    /// <returns><see langword="null"/> when authorized; otherwise the response to return.</returns>
    private static async Task<IResult?> AuthorizeAgentForWorkItemAsync(
        HttpContext httpContext,
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = httpContext.User;
        if (!string.Equals(user.FindFirst("auth_kind")?.Value, "agent", StringComparison.Ordinal))
            return null; // operator — control plane, full access

        var callerAgentId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(callerAgentId))
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var owner = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.AssignedAgentId, w.K8sJobName })
            .FirstOrDefaultAsync(ct);

        if (owner is null)
            return null; // let the handler return 404

        if (string.Equals(owner.AssignedAgentId, callerAgentId, StringComparison.Ordinal) ||
            string.Equals(owner.K8sJobName, callerAgentId, StringComparison.Ordinal))
            return null;

        Log.Warning(
            "Agent {AgentId} denied access to WorkItem {WorkItemId} (assigned to {AssignedAgentId})",
            callerAgentId, id, owner.AssignedAgentId ?? owner.K8sJobName ?? "nobody");
        return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
    }

    // ── GET /{id}/assignment — mirror of monolith ─────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/assignment
    /// Returns the job assignment payload for an agent.
    /// 200 with JobAssignmentMessage, 404 if not found or null payload, 410 if terminal.
    /// <para>
    /// Supports two payload schemas for backward compatibility:
    /// <list type="bullet">
    /// <item>
    /// <term>Old schema (full snapshot)</term>
    /// <description>
    /// Work items created before #2221: <c>Payload</c> contains a full <see cref="JobDistributionRequest"/>
    /// including <c>ProviderConfigs</c>, <c>QualityGateConfigs</c>, etc. Detected by
    /// <c>PayloadSchemaVersion == null</c>. Served directly from payload as before — the frozen
    /// snapshot is returned as-is (tokens may be expired for long-queued items).
    /// </description>
    /// </item>
    /// <item>
    /// <term>New schema (minimal identity)</term>
    /// <description>
    /// Work items created after #2221: <c>Payload</c> contains only identity fields
    /// (<c>PayloadSchemaVersion == 1</c>). Mutable config is fetched fresh from the database
    /// at assignment time via <see cref="AssignmentEnricher"/>, vending fresh tokens and
    /// picking up the latest steering, QG, and pipeline configuration.
    /// </description>
    /// </item>
    /// </list>
    /// </para>
    /// </summary>
    internal static async Task<IResult> GetAssignment(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IProjectStore projectStore,
        AssignmentEnricher? assignmentEnricher = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.Status, w.Payload })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        // Terminal statuses → 410 Gone
        if (item.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            return TypedResults.StatusCode(410);

        if (item.Payload is null)
            return TypedResults.NotFound();

        var request = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload, PipelineJsonOptions.Default);
        if (request is null)
            return TypedResults.NotFound();

        // ── Backward-compatibility: detect payload schema ─────────────────
        // Old schema: PayloadSchemaVersion == null → serve from frozen snapshot.
        // New schema: PayloadSchemaVersion == 1  → fresh-fetch all mutable config.
        if (request.PayloadSchemaVersion == 1 && assignmentEnricher is not null)
        {
            try
            {
                request = await EnrichRequestAsync(request, projectStore, assignmentEnricher, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // EnrichAsync already logged at Error level; return 503 so the agent retries.
                // The WorkItem remains in Dispatched state — the reconciler TTL provides the hard timeout.
                // TODO: [WARNING] For the null-return path (no profile matched), EnrichAsync does NOT log
                // at Error — it returns null after Warning-level logs in EnrichCoreAsync, and then
                // EnrichRequestAsync throws InvalidOperationException which propagates here unlogged at
                // Error level. Add Log.Error(ex, ...) here so all paths producing a 503 have an Error-level
                // trace, regardless of where in the call chain the exception originates. This makes permanent
                // config failures (missing profile, deleted provider) distinguishable from transient failures
                // in alerting dashboards.
                return TypedResults.Problem(
                    detail: "Assignment enrichment failed; please retry.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }
        // TODO: When assignmentEnricher is null and request.PayloadSchemaVersion == 1 (new-schema path),
        // enrichment is silently skipped and an identity-only 200 is returned with no log output.
        // A DI misconfiguration that drops AssignmentEnricher is now undetectable from logs at this site.
        // Restore a Log.Warning when the enricher is null on the new-schema path (was present before #2172).

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(id, request);
        // TODO: [WARNING] InjectProjectSecretsAsync is now called unconditionally for all GetAssignment
        // requests, including old-schema requests where request.PayloadSchemaVersion == null. In the
        // prior code, secret injection was inside the isNewSchema branch. Old-schema callers that log
        // or persist the full response message will now receive live ProjectSecrets where they did not
        // before, widening the exposure surface even though the endpoint already requires Operator auth.
        // Gate this call on request.PayloadSchemaVersion == 1 (new-schema path only), or document the
        // intentional behavior change so callers are aware secrets are now injected on all paths.
        message = await InjectProjectSecretsAsync(message, request, projectStore, ct);

        return TypedResults.Ok(message);
    }

    /// <summary>
    /// Enriches a new-schema request (PayloadSchemaVersion == 1) by fetching mutable config fresh
    /// from the database. Resolves the project for steering + config override context, falling
    /// back to a minimal stub for project-less items.
    /// </summary>
    /// <remarks>
    /// Any exception from <see cref="AssignmentEnricher.EnrichAsync"/> (other than
    /// <see cref="OperationCanceledException"/>) propagates to the caller so it can return HTTP 503.
    /// When <see cref="AssignmentEnricher.EnrichAsync"/> returns <c>null</c> (permanent failure —
    /// profile not found, provider config removed), this also surfaces as a 503 via
    /// <see cref="InvalidOperationException"/> so the agent retries rather than proceeding with
    /// an incomplete job spec.
    /// </remarks>
    private static async Task<JobDistributionRequest> EnrichRequestAsync(
        JobDistributionRequest request,
        IProjectStore projectStore,
        AssignmentEnricher assignmentEnricher,
        CancellationToken ct)
    {
        PipelineProject project;
        if (request.ProjectId.HasValue)
        {
            project = await projectStore.GetProjectByIdAsync(request.ProjectId.Value.ToString(), ct)
                ?? BuildMinimalProject(request);
        }
        else
        {
            project = BuildMinimalProject(request);
        }

        // EnrichAsync propagates transient failures (DB timeout, etc.) — let them bubble up.
        // A null return indicates a permanent/configuration failure (no profile matched).
        var enriched = await assignmentEnricher.EnrichAsync(request, project, ct);
        if (enriched is null)
        {
            // Profile resolution failure is permanent but should still be treated as a 503 so
            // the reconciler TTL can expire the work item rather than the agent silently using
            // an identity-only payload with no configs.
            // TODO: [WARNING] This InvalidOperationException propagates to the GetAssignment catch block
            // which does not log it at Error level (the comment there says "EnrichAsync already logged at
            // Error level", which is true for the transient-exception path but NOT for the null-return
            // path — EnrichCoreAsync only logs at Warning for no-profile-matched). Add Error-level logging
            // here or in the catch block so permanent config failures are visible without querying Warning
            // logs.
            throw new InvalidOperationException(
                $"AssignmentEnricher returned null for IssueIdentifier {request.IssueIdentifier}; " +
                "no agent profile matched the selector. Cannot serve a valid job spec.");
        }

        return enriched;
    }

    /// <summary>
    /// Injects project secrets into the assignment message at delivery time.
    /// Secrets are not serialized in the payload for security; they are fetched fresh here.
    /// </summary>
    private static async Task<JobAssignmentMessage> InjectProjectSecretsAsync(
        JobAssignmentMessage message,
        JobDistributionRequest request,
        IProjectStore projectStore,
        CancellationToken ct)
    {
        // TODO: [WARNING] This method early-returns when request.ProjectId is null, but consolidation
        // work items frequently carry a ConsolidationTemplateId with no ProjectId (the template-owns-project
        // relationship is resolved lazily). ConsolidationWorkItemEndpoints.EnrichPayloadAsync resolves the
        // owning project by template membership when ProjectId is empty, then vends project secrets from that
        // owner. As a result, a TaskType=Consolidation work item with ProjectId=null and a ConsolidationTemplateId
        // whose owning project defines Secrets will get ProjectSecrets=null from this path but populated secrets
        // from the legacy claim path, violating the "fully-enriched" requirement for the unified assignment path.
        // Mirror the template-ownership fallback from ConsolidationWorkItemEndpoints.EnrichPayloadAsync here,
        // or document that /assignment intentionally omits project secrets for project-less consolidation items.
        if (!request.ProjectId.HasValue)
            return message;

        var project = await projectStore.GetProjectByIdAsync(request.ProjectId.Value.ToString(), ct);
        if (project is null)
        {
            // TODO: [WARNING] The structured property name {JobId} is inconsistent with the {WorkItemId}
            // convention used everywhere else in this file (e.g. line 112). Consider renaming to {WorkItemId}
            // for a uniform structured log schema — but note that the integration test at
            // GetAssignmentTests.cs currently asserts ContainKey("JobId"), so both sites must be updated
            // together to avoid a test breakage.
            Log.Warning(
                "InjectProjectSecretsAsync: project {ProjectId} not found — WorkItem {JobId} will run without ProjectSecrets",
                request.ProjectId.Value, message.JobId);
            return message;
        }

        if (project.Secrets is { Count: > 0 })
            return message with { ProjectSecrets = project.Secrets };

        return message;
    }

    /// <summary>
    /// Builds a minimal <see cref="PipelineProject"/> stub for work items without a project ID.
    /// Prevents null-ref in <see cref="AssignmentEnricher.EnrichAsync"/> which requires a non-null project.
    /// </summary>
    private static PipelineProject BuildMinimalProject(JobDistributionRequest request)
        => new()
        {
            Id = request.ProjectId?.ToString() ?? Guid.Empty.ToString(),
            Name = request.ProjectName ?? string.Empty
        };

    // ── POST /{id}/status — mirror of monolith ────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/status
    /// Validates transition via WorkItemTransitionService, updates in-memory state.
    /// 200, 400 (invalid transition), or 404.
    /// </summary>
    // TODO: PostStatus takes WorkItemTransitionService as a concrete type because TransitionDetailedAsync
    // is not declared on any interface (IWorkItemTransitionService only exposes TransitionIfAsync).
    // This prevents interface-level mocking of the transition service in tests; callers must use the
    // concrete class with an in-memory DB. Consider adding TransitionDetailedAsync to an interface
    // (e.g. IWorkItemTransitionService or a new IWorkItemTransitionDetailedService) so PostStatus can
    // be tested with pure mocks and to allow future DI substitution.
    internal static async Task<IResult> PostStatus( // NOSONAR S107 — 8th param is a test-only seam; CA1068 suppressed via attribute below
        Guid id,
        WorkItemStatusRequest request,
        WorkItemTransitionService transitionService,
        IOrchestratorRunService runService,
        IRunLifecycleManager runLifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null,
        CancellationToken ct = default,
        // Test seam only: when true, the telemetry task is awaited before returning so tests can
        // assert metric side-effects deterministically. In production the route lambda never passes
        // this parameter, so it defaults to false and the fire-and-forget path is unchanged.
        // Suppression: CA1068 (ct not last) and S107 (>7 params) are acceptable here because
        // this is an internal method with a test-only parameter appended after the conventional
        // CancellationToken position. Moving the bool before ct would break naming conventions;
        // splitting into an overload doubles the S107 surface area. The bool is never passed by
        // production callers.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Test seam bool appended after ct intentionally")]
        bool awaitTelemetry = false)
    {
        // Infrastructure recovery path (issue #2459): when the agent reports Running, attempt to
        // recover a Failed/Timeout or Failed/InfrastructureFailure item back to Running before
        // reaching TransitionDetailedAsync (which would reject Failed→Running as invalid).
        //
        // This covers the race where ReconciliationLoop's EnforceTimeoutsAsync timed out the item
        // while the agent was still executing — the agent's in-flight PostStatus(Running) arrives
        // after the timeout write and must succeed rather than returning 400.
        //
        // TryRecoverFromInfrastructureFailureAsync returns:
        //   true  — recovery succeeded (or item already at Running) → return 200 immediately.
        //   false — item not found, wrong state, or non-recoverable FailureReason (AgentError etc.)
        //           → fall through to TransitionDetailedAsync for normal handling.
        //
        // When recovery succeeds, we do NOT call lifecycle events (FailRunAsync / CancelRunAsync)
        // because the item is transitioning BACK to an active state, not completing — lifecycle
        // events are only appropriate for terminal transitions below.
        if (request.Status == WorkItemStatus.Running)
        {
            var recovered = await transitionService.TryRecoverFromInfrastructureFailureAsync(
                id, WorkItemStatus.Running, ct: ct);
            if (recovered)
                return TypedResults.Ok();
            // false → not a recoverable race; fall through to TransitionDetailedAsync.
        }

        // Pre-read guard: if the incoming status is terminal and the item is already in a
        // "stronger" terminal state (Cancelled or Succeeded), return Ok silently without calling
        // TransitionDetailedAsync. This suppresses the "Invalid transition" LogWarning that
        // TransitionCoreAsync would otherwise emit for Cancelled→Failed, Succeeded→Failed, etc.
        //
        // This guard exists because "must NOT emit the warning" requires short-circuiting before
        // TransitionCoreAsync is entered — a post-read check (after Rejected is returned) cannot
        // suppress the warning because TransitionCoreAsync emits it before returning Rejected.
        //
        // Trade-off: this pre-read fires for ALL terminal incoming requests, including valid
        // Running→Failed transitions. For those paths GetCurrentStatusAsync returns Running (not
        // Cancelled/Succeeded), so execution falls through to TransitionDetailedAsync as normal —
        // no behavior change except for the extra SELECT. The post-read alternative cannot satisfy
        // the "no warning" acceptance criterion, so the extra read is the required trade-off.
        //
        // TODO: Residual TOCTOU race — the guard reads status in one DB round-trip and
        // TransitionDetailedAsync reads it again in a second. If a concurrent cancellation
        // transitions the item from Running→Cancelled between the two reads, TransitionDetailedAsync
        // will see Cancelled→Failed (invalid) and still emit the spurious warning. This race is
        // narrow (requires a new cancellation to occur within the two-read window for an already-Running
        // item) and represents a residual frequency reduction rather than full elimination. Closing it
        // fully would require a database-level serializable transaction or a new TransitionResult value
        // (e.g. TerminalConflict) returned from TransitionCoreAsync to distinguish "wrong direction"
        // from "already terminal" without a second read. See issue #2461.
        //
        // TODO: If a new terminal WorkItemStatus value is added (e.g. TimedOut), this guard must be
        // updated to include it or idempotency protection will be missing for that status. There is
        // no compile-time exhaustiveness check on this pattern match.
        //
        // See issue #2461.
        // TODO: The early-return condition below checks `currentStatus is Cancelled or Succeeded`
        // but intentionally does NOT include `Failed`. An already-Failed item receiving PostStatus(Failed)
        // falls through to TransitionDetailedAsync, which returns AlreadyAtTarget → HTTP 200 with no
        // warning and no lifecycle call — correct behaviour, but via a different code path than
        // Cancelled→Cancelled and Succeeded→Succeeded. This asymmetry is currently harmless because
        // AlreadyAtTarget never emits a warning. If TransitionCoreAsync's AlreadyAtTarget handling ever
        // changes to emit a log entry, Failed→Failed would be affected while the other same-status
        // combinations would not. Consider including Failed in the guard condition for consistency.
        // See review finding (DotNetSpecialist) for issue #2461.
        if (request.Status is WorkItemStatus.Failed or WorkItemStatus.Cancelled or WorkItemStatus.Succeeded)
        {
            var currentStatus = await transitionService.GetCurrentStatusAsync(id, ct);
            if (currentStatus is WorkItemStatus.Cancelled or WorkItemStatus.Succeeded)
            {
                // TODO: Narrow deleted-item TOCTOU race — GetCurrentStatusAsync returned Cancelled/Succeeded
                // so we return Ok() without calling TransitionDetailedAsync. If the WorkItem was
                // hard-deleted between the GetCurrentStatusAsync read and this return (e.g. in a
                // test/cleanup scenario), the caller receives HTTP 200 for a now-nonexistent item
                // rather than 404. The post-read approach (check after Rejected) would not have this
                // property for the already-terminal case, so this is an accepted trade-off of the
                // pre-read design. To close it, TransitionDetailedAsync would need to return a
                // TerminalConflict result type so the endpoint can distinguish "wrong direction" from
                // "already terminal" without a prior read. See review finding #2 (Correctness) for
                // issue #2461.
                return TypedResults.Ok();
            }
            // null  → item not found; fall through so TransitionDetailedAsync returns NotFound.
            // Any non-terminal current status → fall through for normal processing.
        }

        var transitionResult = await transitionService.TransitionDetailedAsync(
            id, request.Status,
            mutate: entity => ApplyStatusMutation(entity, request),
            ct: ct);

        if (transitionResult == TransitionResult.NotFound)
            return TypedResults.NotFound();

        if (transitionResult == TransitionResult.Rejected)
            return TypedResults.BadRequest("Invalid status transition");

        // Only drive lifecycle events and emit telemetry on an ACTUAL state change.
        // For idempotent no-ops (AlreadyAtTarget), fall through to Ok() silently.
        //
        // FailRunAsync / CancelRunAsync trigger label-swap, dedup-guard, and history writes.
        // Calling them on a repeated PostStatus (e.g. after a leadership flip that clears the
        // jobcontroller's reconciledTerminalIds cache) risks double label swaps and spurious
        // history entries — so they must be gated on TransitionResult.Transitioned, not on
        // success==true as before (which included AlreadyAtTarget).
        if (transitionResult == TransitionResult.Transitioned)
        {
            // For terminal transitions, drive the run through RunLifecycleManager so history,
            // label-swap, registry clear, and dedup-guard are all updated — mirrors what
            // AgentJobLifecycleService does for agent-reported completions. Without this,
            // infrastructure-killed runs (agent disconnect, reconciliation timeout) never appear
            // in IPipelineRunHistoryService and WaitForHistoryAsync in E2E tests times out.
            if (request.Status == WorkItemStatus.Failed)
            {
                var failureReason = request.ErrorMessage ?? request.FailureReason ?? "Infrastructure failure";
                await runLifecycleManager.FailRunAsync(
                    new RunId(id.ToString()),
                    failureReason,
                    ct,
                    CodingAgent.Pipeline.Models.FailureReason.InfrastructureFailure);
            }
            else if (request.Status == WorkItemStatus.Cancelled)
            {
                await runLifecycleManager.CancelRunAsync(new RunId(id.ToString()), ct);
            }

            // Emit telemetry for terminal transitions.
            // Production path (awaitTelemetry=false): fire-and-forget so the enrichment DB read
            // does not block the agent's 200 response and a slow/failed read does not surface as a 500.
            // Test path (awaitTelemetry=true): task is awaited before returning, eliminating the
            // Task.Delay race that made telemetry-asserting tests flaky on loaded CI hosts.
            // CancellationToken.None is intentional: this task outlives the HTTP request lifetime;
            // using the request-scoped ct would cause spurious OperationCanceledException warnings
            // when ASP.NET Core cancels the token as soon as the response is sent.
            if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            {
                var emitTask = EmitTerminalStatusTelemetryAsync(id, request, dbFactory, CancellationToken.None);
                if (awaitTelemetry)
                    await emitTask;
                else
                    _ = emitTask;
            }
        }

        return TypedResults.Ok();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static void ApplyStatusMutation(WorkItemEntity entity, WorkItemStatusRequest request)
    {
        if (request.AgentId is not null)
            entity.AssignedAgentId = request.AgentId;

        if (request.ErrorMessage is not null)
            entity.ErrorMessage = request.ErrorMessage;
        else if (request.Status == WorkItemStatus.Failed)
            entity.ErrorMessage = "Job failed without specific error information";

        if (request.Result is not null)
            entity.Result = request.Result;

        if (request.BranchName is not null)
            entity.BranchName = request.BranchName;

        if (request.Status == WorkItemStatus.Failed)
        {
            if (request.FailureReason is not null
                && Enum.TryParse<FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason)
                && Enum.IsDefined(typeof(FailureReason), parsedReason))
            {
                entity.FailureReason ??= parsedReason;
            }
            else
            {
                entity.FailureReason ??= FailureReason.AgentError;
            }
        }

        if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            entity.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static async Task EmitTerminalStatusTelemetryAsync(
        Guid id,
        WorkItemStatusRequest request,
        IDbContextFactory<PipelineDbContext>? dbFactory,
        CancellationToken ct = default)
    {
        try
        {
            TimeSpan? duration = null;
            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var item = await db.WorkItems.AsNoTracking()
                    .Where(w => w.Id == id)
                    .Select(w => new { w.DispatchedAt, w.CompletedAt })
                    .FirstOrDefaultAsync(ct);
                if (item?.DispatchedAt is not null && item.CompletedAt is not null)
                    duration = item.CompletedAt.Value - item.DispatchedAt.Value;
            }

            // Enum.TryParse succeeds for numeric string inputs (e.g. "99") even when they don't
            // correspond to a named FailureReason member, yielding an undefined enum instance that
            // would become a high-cardinality metric tag. The IsDefined guard rejects such values
            // so only named members reach the telemetry dimension. (Issue #2341)
            FailureReason? failureReason = Enum.TryParse<FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason)
                && Enum.IsDefined(typeof(FailureReason), parsedReason)
                ? parsedReason
                : (FailureReason?)null;

            WorkDistributionTelemetry.LogTerminalStatus(
                id, request.Status, duration, request.AgentId,
                failureReason);
        }
        catch (Exception ex)
        {
            // TODO: [WARNING] The SourceContext here is now "WorkItemAgentEndpoints" (renamed from
            // "WorkItemEndpoints" when the monolith was split). Any alerting rules, log queries, or
            // dashboards that filter on SourceContext = "WorkItemEndpoints" will silently stop matching
            // after this refactor. Update any log-based alert conditions or Kibana/Grafana queries that
            // reference the old class name.
            Serilog.Log.ForContext("SourceContext", nameof(WorkItemAgentEndpoints))
                .Warning(ex, "Failed to emit terminal status telemetry for WorkItem {Id}", id);
        }
    }
}

/// <summary>
/// Request body for POST /api/work-items/{id}/status.
/// Mirrors the monolith's WorkItemStatusRequest.
/// </summary>
public sealed class WorkItemStatusRequest
{
    public required WorkItemStatus Status { get; init; }
    public string? AgentId { get; init; }
    public string? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FailureReason { get; init; }
    public string? BranchName { get; init; }
}
