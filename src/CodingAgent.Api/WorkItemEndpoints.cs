using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints for the Work Item HTTP API.
/// A superset of the monolith's WorkItemEndpoints, adding claim, requeue, retry-count, pending, and staleness.
///
/// Two tiers: <c>/{id}/assignment</c> and <c>/{id}/status</c> accept agent-derived keys but bind
/// the caller to the work item it was dispatched for; every other route is control plane and
/// requires the operator key.
/// </summary>
public static class WorkItemEndpoints
{
    /// <summary>
    /// Maps work item endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemEndpoints(this IEndpointRouteBuilder app)
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

        // ── Control-plane endpoints ────────────────────────────────────────
        // Called by the Job Controller and the monolith, which authenticate with the master
        // key (operator tier). No agent pod calls these, so agent-derived keys are refused
        // outright — otherwise a single compromised pod could claim, requeue or enumerate
        // every work item in the cluster.
        group.MapPost("/", CreateWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/pending", GetPendingWorkItems).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/active", GetActiveWorkItems).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/claim", ClaimWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/dispatch", DispatchPendingWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/requeue", RequeueWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/{id:guid}/retry-count", GetRetryCount).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/staleness", GetStaleness).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/label-swap", PostLabelSwap).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/last-progress", PostLastProgress).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/priority", PostPriorityWeight).RequireAuthorization(ApiAuthPolicies.Operator);

        // Synchronous dispatch endpoint — called by KubernetesWorkDistributor instead of POST /.
        // Atomically performs PVC selection, K8s Job creation, and Dispatched state write in a
        // single request. Returns 200+WorkItemId on success, 409 if at concurrency limit,
        // 503 if no PVC available or K8s failure.
        group.MapPost("/dispatch", DispatchWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);

        // ── Metrics feed for the Scheduler's WorkItemCountsPoller ─────────────
        group.MapGet("/counts-by-status", GetCountsByStatus).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/{id:guid}/k8s-job-name", GetK8sJobName).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/{id:guid}/status", GetWorkItemStatus).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/is-distributed", GetIsDistributed).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/active-identifiers", GetActiveIdentifiers).RequireAuthorization(ApiAuthPolicies.Operator);
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
        if (!request.ProjectId.HasValue)
            return message;

        var project = await projectStore.GetProjectByIdAsync(request.ProjectId.Value.ToString(), ct);
        if (project?.Secrets is { Count: > 0 })
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

    // ── POST / — create WorkItem ───────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items
    /// Creates a new WorkItem with Status=Pending from a JobDistributionRequest.
    /// Also materialises an in-memory <see cref="PipelineRun"/> in <see cref="IOrchestratorRunService"/>
    /// so the UI can show the run immediately (Option A, Req 1a.1).
    /// Returns 201 + new GUID. Maps Postgres 23505 unique violation to 409 Conflict.
    /// <para>
    /// Only identity fields are serialized to <c>WorkItems.Payload</c> (issue #2171).
    /// Mutable config (ProviderConfigs, QualityGateConfigs, RepoSteeringContent, etc.) is stripped
    /// from the stored payload; <c>GET /api/work-items/{id}/assignment</c> fetches it fresh at
    /// dispatch time via <see cref="AssignmentEnricher"/>.
    /// </para>
    /// </summary>
    internal static async Task<IResult> CreateWorkItem(
        [FromBody] JobDistributionRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        CancellationToken ct = default)
    {
        // Use RunId from request if provided (ensures WorkItem.Id == PipelineRun.RunId for hub routing).
        // Fall back to a new GUID when no RunId is set (e.g., direct API calls without orchestration).
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        // Serialize only identity fields to the payload (issue #2171).
        // Mutable config (ProviderConfigs, QGs, steering, MCP servers, issue context) is fetched
        // fresh at GetAssignment time. This prevents stale config being served to agents that
        // were queued for extended periods.
        var minimalPayload = BuildMinimalPayload(request);
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Pending,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector ?? "",
            // TODO: Add a positive-value guard here: if request.TimeoutSeconds <= 0, substitute
            // (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds. This prevents a legacy or
            // misconfigured caller from storing a zero (the DB column default) and relying on the
            // dispatch-path fallback in BuildJobContext. See review finding [WARNING] — zero sentinel
            // ambiguity in ReconciliationLoop and DispatchLoop.
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow,
            PriorityWeight = InitiatedByConstants.IsManual(request.InitiatedBy) ? 100 : 0,
            // Capture the W3C traceparent from the current API span so the worker K8s Job
            // can restore it and attach its spans to this trace rather than starting a new root.
            // Activity.Current here is the ASP.NET Core request span — the API span that the
            // caller's trace is already a child of — which is exactly the correct parent.
            // Exception: when the request carries a pre-stored TraceContext (e.g., consolidation
            // rehydration at startup where Activity.Current is null), prefer that instead.
            TraceParent = request.TraceContext?.GetValueOrDefault("traceparent")
                ?? PipelineTelemetry.FormatTraceParent(Activity.Current)
        };

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Distinguish idempotent PK retry from business-rule unique index conflict.
            // If a row with this workItemId already exists, this is a safe idempotent retry —
            // return 201 so EnsureSuccessStatusCode() on the retried response succeeds.
            //
            // Note: Postgres throws DbUpdateException (SQLSTATE 23505); EF InMemory throws
            // ArgumentException("An item with the same key has already been added") directly,
            // so the catch must be on Exception rather than DbUpdateException.
            await using var readDb = await dbFactory.CreateDbContextAsync(ct);
            var exists = await readDb.WorkItems.AnyAsync(w => w.Id == workItemId, ct);
            if (exists)
                return TypedResults.Created($"/api/work-items/{workItemId}", workItemId);

            // Postgres 23505: partial unique index on (IssueIdentifier, IssueProviderConfigId)
            // for non-terminal statuses — a different run is already live for this issue.
            return TypedResults.Conflict("A live work item already exists for this issue.");
        }

        // Materialise in-memory PipelineRun in the API's IOrchestratorRunService so the UI
        // can subscribe to hub events and display the run immediately (Req 1a.1 Option A).
        // WorkItem.Id == PipelineRun.RunId for deterministic hub-group routing.
        // Consolidation WorkItems return null — they are tracked via ConsolidationRun, not PipelineRun.
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
        if (run is not null)
            runService.AddRun(run);

        return TypedResults.Created($"/api/work-items/{workItemId}", workItemId);
    }

    /// <summary>
    /// Builds a minimal <see cref="JobDistributionRequest"/> containing only identity fields
    /// that are stored in <c>WorkItems.Payload</c>. Strips all mutable config that will be
    /// re-fetched at assignment time.
    /// </summary>
    internal static JobDistributionRequest BuildMinimalPayload(JobDistributionRequest request)
    {
        return new JobDistributionRequest
        {
            // Identity / non-reconstructable fields
            IssueIdentifier = request.IssueIdentifier,
            IssueProviderConfigId = request.IssueProviderConfigId,
            RepoProviderConfigId = request.RepoProviderConfigId,
            BrainProviderConfigId = request.BrainProviderConfigId,
            PipelineProviderConfigId = request.PipelineProviderConfigId,
            InitiatedBy = request.InitiatedBy,
            TaskType = request.TaskType,
            AgentSelector = request.AgentSelector,
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            RunType = request.RunType,
            RunId = request.RunId,
            TraceContext = request.TraceContext,

            // Audit / routing identity
            // (ProjectName and InitiatedBy also kept above for GetPendingWorkItems display)

            // Review-specific identity (not trivially re-fetchable at assignment time)
            LinkedPullRequest = request.LinkedPullRequest,
            ReviewPrTargetBranch = request.ReviewPrTargetBranch,
            ReviewPrDescription = request.ReviewPrDescription,
            ReviewPrAuthor = request.ReviewPrAuthor,

            // Decomposition identity
            // ProjectContext is pre-built by DispatchOrchestrationService.BuildDecompositionProjectContextAsync
            // and consumed by WriteProjectContextStep, CloneProjectRepositoriesStep, DecompositionAnalysisStep,
            // CreateSubIssuesStep, and AgentProviderResolver. It cannot be reconstructed at assignment time.
            ProjectContext = request.ProjectContext,
            DecompositionSource = request.DecompositionSource,

            // Review-specific pre-fetched context
            // LinkedIssueContexts is pre-fetched by DispatchOrchestrationService and consumed by
            // ExtractLinkedIssuesStep and JobAssignmentMessageFactory. It is not re-fetched by
            // AssignmentEnricher.EnrichCoreAsync, so it must be preserved here.
            LinkedIssueContexts = request.LinkedIssueContexts,

            // Consolidation identity
            ConsolidationRunType = request.ConsolidationRunType,
            ConsolidationTemplateId = request.ConsolidationTemplateId,
            ConsolidationWorkspacePath = request.ConsolidationWorkspacePath,
            AutoDispatch = request.AutoDispatch,

            // Issue title kept for GetPendingWorkItems display (not mutable config)
            IssueDetail = request.IssueDetail is not null
                ? new IssueDetail
                {
                    Identifier = request.IssueDetail.Identifier,
                    Title = request.IssueDetail.Title,
                    Description = string.Empty, // Strip body; re-fetched at assignment time
                    Labels = []
                }
                : null,

            // Schema version discriminator — marks this as new-schema (minimal identity payload).
            // GetAssignment uses PayloadSchemaVersion == 1 to detect new-schema rows and trigger
            // AssignmentEnricher. All mutable config intentionally omitted (null). Fields not listed
            // above (ProviderConfigs, PipelineConfiguration, QualityGateConfigs, ReviewerConfigs,
            // McpServers, RepoSteeringContent, ProjectSteeringContent, IssueComments,
            // ParsedIssue, ExistingAnalysis, ResolvedProfileId, AgentProviderConfigId)
            // are left at their default null values and re-fetched at assignment time by AssignmentEnricher.
            PayloadSchemaVersion = 1,
        };
    }

    // ── POST /{id}/dispatch — claim-by-CAS dispatch for a single-owner Scheduler ──

    /// <summary>
    /// POST /api/work-items/{id}/dispatch
    /// Claims an existing <c>Pending</c> WorkItem by CAS and creates the K8s Job atomically.
    /// Designed for the leader-elected Scheduler poller (issue #2541) which enqueues items as
    /// <c>Pending</c> via <c>POST /api/work-items</c> and then dispatches them via this endpoint.
    ///
    /// <para>
    /// Wraps the snapshot → gate → CAS in a Postgres advisory lock keyed on the normalized
    /// agent selector to prevent the over-dispatch window where two concurrent calls for
    /// different Pending items of the same selector both observe capacity available.
    /// </para>
    ///
    /// <para>
    /// Unlike <c>POST /api/work-items/dispatch</c> (<see cref="DispatchWorkItem"/>), this endpoint
    /// does NOT create a new WorkItem — it claims an already-Pending row.
    /// </para>
    ///
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 — dispatch succeeded (K8s Job running, WorkItem=Dispatched)</item>
    ///   <item>404 Not Found — no WorkItem with this ID</item>
    ///   <item>409 Conflict — item not Pending, concurrency limit reached, or no template for selector</item>
    ///   <item>503 Service Unavailable — no PVC available, advisory lock timeout, or K8s Job creation failed</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> DispatchPendingWorkItem(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateStore,
        DispatchTemplateResolver templateResolver,
        IDistributedLockProvider lockProvider,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Fast path: check status before acquiring the lock.
        // Not an atomic guarantee — the CAS inside ExecuteDispatchLifecycleAsync is the
        // correctness guard. This check avoids unnecessary lock contention for items that
        // are already Dispatched or in a terminal state.
        var quickCheck = await db.WorkItems.AsNoTracking()
            .Select(w => new { w.Id, w.Status, w.AgentSelector, w.TimeoutSeconds, w.TaskType, w.ProjectId, w.IssueIdentifier, w.IssueProviderConfigId, w.PriorityWeight, w.CreatedAt })
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (quickCheck is null)
            return TypedResults.NotFound();

        if (quickCheck.Status != WorkItemStatus.Pending)
        {
            Log.Information("DispatchPendingWorkItem: WorkItem {WorkItemId} is not Pending (status={Status}) — returning 409",
                id, quickCheck.Status);
            return TypedResults.Conflict($"Work item {id} is not in Pending state (current status: {quickCheck.Status}).");
        }

        var agentSelector = quickCheck.AgentSelector;
        var normalizedSelector = JobTemplateStore.NormalizeLabels(agentSelector);

        // TODO [WARNING]: agentSelector originates from a database column set by POST /api/work-items callers.
        // It is embedded verbatim in Conflict response bodies (e.g. "No job template for agent selector: {value}")
        // and passed directly to Log.Warning / Log.Information calls below. A crafted selector containing
        // newline characters (\r\n) can inject fake log lines, obscuring audit trails. A selector containing
        // HTML/script markup is reflected in the 409 response body (Content-Type is text/plain for minimal-API
        // Conflict<string>, which reduces XSS risk, but does not eliminate it for browser-side consumers).
        // Validate and/or sanitize agentSelector at the WorkItem write boundary, or at minimum truncate/escape
        // the value before embedding it in log messages and response strings.

        // Acquire advisory lock keyed on the normalized selector.
        // This serialises all concurrent calls to this endpoint for the same selector,
        // preventing the over-dispatch window where two callers both see capacity available
        // and each dispatch a different Pending item for the same selector.
        //
        // Lock scope: snapshot → gate → CAS (inside ExecuteDispatchLifecycleAsync).
        // The label swap to agent:in-progress is NOT in scope — it happens in AgentHub.RegisterAgent.
        //
        // Note: this lock only protects concurrent calls to THIS endpoint. DispatchWorkItem
        // (collection-level) and WorkItemDispatchService (background poller) do NOT acquire
        // this lock. Cross-path correctness is provided by the CAS in ExecuteDispatchLifecycleAsync.
        // TODO [WARNING]: The two-variable lock pattern (IAsyncDisposable lockHandle; ... await using var _ = lockHandle)
        // is non-idiomatic. Prefer a try/finally or helper wrapper if this method is refactored, to
        // ensure the handle is always disposed even under unexpected async state-machine faults.
        IAsyncDisposable lockHandle;
        try
        {
            lockHandle = await lockProvider.AcquireAsync($"dispatch-selector:{normalizedSelector}", ct);
        }
        catch (TimeoutException)
        {
            // PostgresDistributedLockProvider retries with pg_try_advisory_lock for up to 60s.
            // A timeout means another call has held the lock for that entire window (e.g. a slow
            // K8s API response). Return 503 — transient, the Scheduler should retry next cycle.
            Log.Warning("DispatchPendingWorkItem: advisory lock acquisition timed out for selector {Selector} — returning 503", normalizedSelector);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var _ = lockHandle;

        // TODO [WARNING]: The item's status is not re-read after acquiring the lock. If another
        // dispatch path (WorkItemDispatchService or DispatchWorkItem) transitioned the item from
        // Pending to Dispatched between the fast-path check and lock acquisition, the gates may
        // pass and the lifecycle CAS will then abort (returning !dispatched → 503). The CAS
        // preserves correctness, but callers receive a transient 503 instead of a permanent 409,
        // which may cause unnecessary Scheduler retries. Consider re-checking Status inside the
        // lock and returning 409 immediately if the item is no longer Pending.

        // Build concurrency map inside the lock with normalised keys.
        // NOTE: do NOT use DispatchStateBuilder.BuildStateAsync here — it does NOT normalise
        // keys (uses raw x.Selector). Follow the DispatchWorkItem pattern (NormalizeLabels on
        // each key) so active items stored by any path are counted correctly.
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var concurrencyBySelector = activeCounts.ToDictionary(
            x => JobTemplateStore.NormalizeLabels(x.Selector),
            x => x.Count,
            StringComparer.Ordinal);

        // Query PVC availability.
        var pvcPool = lifecycle.GetPvcPool();
        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, ct);
        // TODO [WARNING]: pvcResult.AvailablePvcs is the mutable list passed into DispatchLifecycleContext below.
        // The lifecycle may mutate it internally (re-adding a PVC on a race). This is the same contract as
        // DispatchWorkItem. If the PVC gate or metric call is ever moved *after* the lifecycle call, the count
        // would be stale. Keep the PVC gate and metric emission before the lifecycle call.

        // Emit credential-pool gauge BEFORE the PVC gate so the metric is always updated
        // whenever the PVC query runs (including on PVC-exhaustion 503).
        // Deliberate exception to the BuildStateAsync restriction: this endpoint is the
        // Scheduler-driven dispatch path and is the appropriate emitter for this metric.
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(pvcResult.AvailablePvcs.Count, pvcResult.ClaimedCount);

        // Template gate — try direct resolve first, then profile-based fallback.
        // TODO [WARNING]: Mixed agentSelector (raw) vs normalizedSelector usage below is intentional —
        // the profile lookup uses the raw selector to match MatchLabels, while template store uses the
        // normalized form. If the profile fallback resolves a template, projection.AgentSelector will be
        // normalizedSelector (the raw item selector, normalized) rather than the template's canonical
        // labels (e.g. "dotnet" vs "dotnet,kiro"). This causes the concurrency map key to differ from
        // keys stored for items dispatched via the direct-resolve path, potentially under-counting
        // active items in the concurrency gate on the profile-fallback path. See also the concurrency
        // tracking mismatch note on projection.AgentSelector below.
        var template = templateStore.Resolve(normalizedSelector);
        if (template is null)
        {
            var (fallbackTemplate, _) = await templateResolver.ResolveTemplateViaProfileAsync(
                agentSelector, "DispatchPendingWorkItem", ct);
            template = fallbackTemplate;
        }

        if (template is null)
        {
            Log.Warning("DispatchPendingWorkItem: no job template for selector {Selector} — returning 409", agentSelector);
            return TypedResults.Conflict($"No job template for agent selector: {agentSelector}");
        }

        // Concurrency gate.
        if (DispatchStateBuilder.IsAtConcurrencyLimit(normalizedSelector, concurrencyBySelector, template.MaxConcurrent))
        {
            var currentCount = concurrencyBySelector.GetValueOrDefault(normalizedSelector, 0);
            Log.Information("DispatchPendingWorkItem: concurrency limit reached for selector {Selector} ({Current}/{Max}) — returning 409",
                agentSelector, currentCount, template.MaxConcurrent);
            return TypedResults.Conflict($"Concurrency limit reached for selector '{agentSelector}' ({currentCount}/{template.MaxConcurrent}).");
        }

        // PVC gate.
        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);
        if (isKiroAgent && pvcResult.AvailablePvcs.Count == 0)
        {
            WorkDistributionTelemetry.PvcPoolExhaustions.Add(1);
            Log.Information("DispatchPendingWorkItem: no PVC available for kiro agent selector {Selector} — returning 503", agentSelector);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Build the projection for ExecuteDispatchLifecycleAsync.
        // TODO [WARNING]: When the profile fallback resolves the template, projection.AgentSelector is
        // set to normalizedSelector (e.g. "dotnet"), not to the template's canonical labels (e.g. "dotnet,kiro").
        // FinalizeDispatchAsync will increment concurrencyBySelector["dotnet"] rather than ["dotnet,kiro"].
        // Active items stored with selector "kiro,dotnet" are counted under a different normalized key
        // ("dotnet,kiro"), so IsAtConcurrencyLimit may under-count on the profile-fallback path and
        // allow over-dispatch when maxConcurrent is tight. The same gap exists in FinalizeDispatchAsync
        // (see its // TODO: Use effectiveSelector comment). Fix both together when the effectiveSelector
        // propagation is resolved.
        var projection = new PendingWorkItemProjection
        {
            Id = id,
            AgentSelector = normalizedSelector,
            CreatedAt = quickCheck.CreatedAt,
            TimeoutSeconds = quickCheck.TimeoutSeconds,
            TaskType = quickCheck.TaskType,
            ProjectId = quickCheck.ProjectId,
            IssueIdentifier = quickCheck.IssueIdentifier,
            IssueProviderConfigId = quickCheck.IssueProviderConfigId,
            PriorityWeight = quickCheck.PriorityWeight
        };

        // ExpectedInitialStatus is left at the default (Pending) — do NOT copy the
        // "ExpectedInitialStatus = WorkItemStatus.Dispatched" override from DispatchWorkItem.
        // That override applies because DispatchWorkItem creates items directly as Dispatched.
        // Here the item already exists as Pending; the lifecycle race-guard must see Pending
        // after K8s Job creation to proceed — setting it to Dispatched would cause the guard
        // to treat the Pending row as a race and delete the K8s Job silently.
        var ctx = new DispatchLifecycleContext(
            db,
            projection,
            template,
            isKiroAgent,
            pvcResult.AvailablePvcs,
            concurrencyBySelector,
            "pending-dispatch ");

        bool dispatched = false;
        try
        {
            await lifecycle.ExecuteDispatchLifecycleAsync(
                ctx,
                // TODO [WARNING]: The outer endpoint CancellationToken ct is captured by this lambda.
                // If the HTTP client disconnects and cancels ct while ExecuteDispatchLifecycleAsync is
                // mid-flight, LoadProjectSecretsAsync will throw OperationCanceledException. The lifecycle's
                // outer catch (when ex is not OperationCanceledException) correctly re-throws it, and
                // the advisory lock handle is still disposed via await using var _ = lockHandle on the
                // stack — no deadlock or resource leak. This comment documents the cancellation contract:
                // ct cancellation propagates as OperationCanceledException to the caller, not as 503.
                prepareVariant: workItem => PrepareDispatchVariantAsync(db, workItem, ct),
                onDispatchSuccess: _ =>
                {
                    dispatched = true;
                    // Label swap to agent:in-progress is handled by AgentHub.RegisterAgent when
                    // the agent connects. No action needed here — same as WorkItemDispatchService.
                    return Task.CompletedTask;
                },
                ct,
                onFailure: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "DispatchPendingWorkItem: unhandled exception during dispatch lifecycle for {WorkItemId}", id);
            // TODO [WARNING]: This catch swallows all non-cancellation exceptions from ExecuteDispatchLifecycleAsync,
            // including ObjectDisposedException (PipelineDbContext disposed prematurely) and ArgumentNullException
            // (programming errors in the lifecycle), returning 503 for all of them. This masks bugs that would
            // otherwise surface as 500s during development. This pattern is consistent with DispatchWorkItem but
            // is a correctness concern for observability — consider narrowing the catch to known transient
            // exceptions (HttpRequestException, DbException) and re-throwing programming errors.
            // TODO [WARNING]: The lifecycle item state after an exception here is not always Failed.
            // If the exception fires after K8s Job creation but before the Dispatched-status save,
            // the K8s Job may be running while the item is still Pending, enabling a double-dispatch
            // on the next Scheduler poll cycle. This window is inherited from the lifecycle service
            // itself (not introduced by this endpoint) but is worth fixing when the lifecycle's
            // orphan-detection is hardened. For now, return 503 — the Scheduler retry will re-enter
            // the CAS which will correctly detect the now-Dispatched item and abort.
            // The lifecycle may have already transitioned the item to Failed.
            // Do NOT call SafelyCancelOrphanedDispatchedWorkItemAsync here — the item started
            // as Pending, not Dispatched, so there is no orphaned Dispatched row to cancel.
            // FailWorkItemAsync (Pending→Failed) was already called internally on K8s failure.
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!dispatched)
        {
            // ExecuteDispatchLifecycleAsync returned without dispatching.
            // TODO [WARNING]: The item state here is not always Failed. Two additional !dispatched
            // paths leave the item Pending: (1) SelectPvcAsync returned null under _pvcSelectLock
            // (PVC race — item untouched), and (2) DbUpdateConcurrencyException on the pre-write
            // save (item untouched). In those cases the 503 is correct (item remains Pending for
            // the next poll cycle), but the comment below overstates the guarantee.
            // Additionally, on path (2) if the pre-write wrote ClaimedPvcName to the Pending row,
            // QueryAvailablePvcsAsync will report that PVC as claimed on all subsequent calls until
            // ReconciliationService reconciles the item, potentially exhausting the PVC pool.
            // For a Pending-initial item this means: PVC race (another replica claimed the PVC
            // under _pvcSelectLock) or K8s Job creation failed (lifecycle already called
            // FailWorkItemAsync, transitioning the item to Failed).
            // Unlike DispatchWorkItem, there is no orphaned Dispatched row to clean up here —
            // the item was never written as Dispatched before the K8s call.
            Log.Warning("DispatchPendingWorkItem: lifecycle did not dispatch WorkItem {WorkItemId} (PVC race or K8s failure) — returning 503", id);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Ok(id);
    }

    // ── POST /dispatch — synchronous dispatch endpoint ────────────────────

    /// <summary>
    /// POST /api/work-items/dispatch
    /// Synchronous dispatch path: atomically performs PVC selection, K8s Job creation, and
    /// <c>Dispatched</c> state write in a single request. Called by <c>KubernetesWorkDistributor</c>
    /// instead of the two-step <c>POST /api/work-items</c> + DispatchLoop path.
    ///
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 + WorkItemId — dispatch succeeded (K8s Job running, WorkItem=Dispatched)</item>
    ///   <item>409 Conflict — concurrency limit reached for this selector</item>
    ///   <item>503 Service Unavailable — no PVC available or K8s Job creation failed</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> DispatchWorkItem(
        [FromBody] JobDistributionRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Template resolution: selector → JobTemplate
        var template = templateStore.Resolve(request.AgentSelector ?? "");
        if (template is null)
        {
            Log.Warning("DispatchWorkItem: no job template for selector {Selector} — returning 409",
                request.AgentSelector);
            return TypedResults.Conflict($"No job template for agent selector: {request.AgentSelector}");
        }

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Build concurrency map (dispatched/running items by selector)
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        // Normalise stored selectors before building the lookup key so that items written by
        // any path (including the old DispatchLoop which may have normalised differently) are
        // counted correctly. NormalizeLabels is idempotent — normalising an already-normalised
        // key is a no-op.
        var concurrencyBySelector = activeCounts.ToDictionary(
            x => JobTemplateStore.NormalizeLabels(x.Selector),
            x => x.Count,
            StringComparer.Ordinal);

        // Concurrency gate
        var maxConcurrent = template.MaxConcurrent;
        if (maxConcurrent > 0)
        {
            var lookupKey = JobTemplateStore.NormalizeLabels(request.AgentSelector ?? "");
            var currentCount = concurrencyBySelector.GetValueOrDefault(lookupKey, 0);
            if (currentCount >= maxConcurrent)
            {
                Log.Information("DispatchWorkItem: concurrency limit reached for selector {Selector} ({Current}/{Max}) — returning 409",
                    request.AgentSelector, currentCount, maxConcurrent);
                return TypedResults.Conflict($"Concurrency limit reached for selector '{request.AgentSelector}' ({currentCount}/{maxConcurrent}).");
            }
        }

        // PVC gate: kiro agents require an available credential PVC
        // TODO [WARNING]: This PVC availability snapshot is taken OUTSIDE _pvcSelectLock. Two concurrent
        // requests can both observe availablePvcs.Count > 0, pass the gate, create their Dispatched rows,
        // and both enter ExecuteDispatchLifecycleAsync. SelectPvcAsync (inside the lock) dequeues from
        // each caller's in-memory availablePvcs list — it does NOT re-query the database. In a
        // single-process deployment _pvcSelectLock ensures exactly one 200 and one 503, because all
        // callers share the same in-memory list. In a multi-replica deployment each pod holds its own
        // list; _pvcSelectLock cannot serialise across replicas. Both replicas can dequeue the same PVC
        // and create a K8s Job. The race is detected by HandleOrphanedJobIfRaceDetectedAsync (status
        // mismatch after K8s Job creation) or a DbUpdateConcurrencyException in FinalizeDispatchAsync.
        // SafelyCancelOrphanedDispatchedWorkItemAsync transitions the orphaned Dispatched row to Failed.
        // The 503 is self-healing: the issue label never advanced to agent:in-progress on this path,
        // so it stays agent:next and the Scheduler re-picks it on the next poll (default 60s).
        // See docs/architecture/concurrency-model.md — "PVC Dispatch Race in Multi-Replica Deployments".
        // A distributed lock (Postgres advisory lock) would be required to guarantee the one-200/one-503
        // invariant across replicas.
        var pvcPool = lifecycle.GetPvcPool();
        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, ct);
        var availablePvcs = pvcResult.AvailablePvcs;

        if (isKiroAgent && availablePvcs.Count == 0)
        {
            Log.Information("DispatchWorkItem: no PVC available for kiro agent selector {Selector} — returning 503",
                request.AgentSelector);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Capacity checks passed — create the WorkItem directly as Dispatched.
        // Issue #2322 requirement: no WorkItem is ever written as Pending on the live dispatch path.
        // The item is created as Dispatched atomically with the lifecycle call that creates the K8s Job.
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        var minimalPayload = BuildMinimalPayload(request);
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow,
            Payload = payloadJson,
            AgentSelector = JobTemplateStore.NormalizeLabels(request.AgentSelector ?? ""),
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow,
            PriorityWeight = InitiatedByConstants.IsManual(request.InitiatedBy) ? 100 : 0,
            TraceParent = request.TraceContext?.GetValueOrDefault("traceparent")
                ?? PipelineTelemetry.FormatTraceParent(Activity.Current)
        };

        try
        {
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Idempotent retry: a work item with this ID already exists.
            // Return 200 only when the existing item is still active (Dispatched or Running).
            // For terminal or Pending states, return 409 so the Scheduler re-queues the issue
            // rather than treating it as dispatched — a Failed/Cancelled item has no K8s Job
            // and returning 200 would cause DispatchOrchestrationService to swap the GitHub
            // label to agent:in-progress with no running job.
            //
            // Use a fresh DbContext — after DbUpdateException the original context is in a faulted state
            // and further queries on it may return stale results or fail.
            await using var freshDb = await dbFactory.CreateDbContextAsync(ct);
            var existing = await freshDb.WorkItems.AsNoTracking()
                .Select(w => new { w.Id, w.Status })
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (existing is not null)
            {
                var isActive = existing.Status is WorkItemStatus.Dispatched or WorkItemStatus.Running;
                if (isActive)
                    return TypedResults.Ok(workItemId);
                Log.Warning("DispatchWorkItem: idempotent retry for {WorkItemId} but existing item is in non-active state {Status} — returning 409",
                    workItemId, existing.Status);
                return TypedResults.Conflict($"Work item {workItemId} already exists in non-active state {existing.Status}.");
            }
            return TypedResults.Conflict("A live work item already exists for this issue.");
        }

        // Register PipelineRun so the UI can subscribe to hub events immediately.
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
        // TODO [WARNING]: If PipelineRunFactory.CreateFromWorkItem returns null, the WorkItem will be
        // dispatched (K8s Job running, WorkItem=Dispatched) but no PipelineRun is registered in
        // IOrchestratorRunService. The UI will not receive live run events for this WorkItem. The
        // requirement states registration is mandatory for SignalR hub routing. Add a log warning
        // and investigate why CreateFromWorkItem returns null (missing required fields in the request).
        if (run is not null)
            runService.AddRun(run);

        // Projection for ExecuteDispatchLifecycleAsync
        var projection = new PendingWorkItemProjection
        {
            Id = workItemId,
            AgentSelector = entity.AgentSelector,
            CreatedAt = entity.CreatedAt,
            TimeoutSeconds = entity.TimeoutSeconds,
            TaskType = entity.TaskType,
            ProjectId = entity.ProjectId,
            IssueIdentifier = entity.IssueIdentifier,
            IssueProviderConfigId = entity.IssueProviderConfigId,
            PriorityWeight = entity.PriorityWeight
        };

        var ctx = new DispatchLifecycleContext(
            db,
            projection,
            template,
            isKiroAgent,
            availablePvcs,
            concurrencyBySelector,
            "sync-dispatch ")
        {
            // The WorkItem was created directly as Dispatched (issue #2322: no Pending write on live path).
            // The lifecycle race-guard checks this status when reloading the item after K8s Job creation.
            ExpectedInitialStatus = WorkItemStatus.Dispatched
        };

        // Track whether dispatch succeeded so we can return the correct status.
        bool dispatched = false;
        try
        {
            await lifecycle.ExecuteDispatchLifecycleAsync(
                ctx,
                prepareVariant: workItem => PrepareDispatchVariantAsync(db, workItem, ct),
                onDispatchSuccess: _ =>
                {
                    dispatched = true;
                    // Label swap to agent:in-progress is handled by DispatchOrchestrationService
                    // after this endpoint returns 200. No action needed here.
                    return Task.CompletedTask;
                },
                ct,
                onFailure: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "DispatchWorkItem: unhandled exception during dispatch lifecycle for {WorkItemId}", workItemId);
            // Clean up the item: if the lifecycle didn't already transition it to Failed,
            // cancel it now so it doesn't remain as an orphaned Dispatched item forever.
            // DispatchLoop no longer exists to pick up orphaned items on the live dispatch path.
            // Use CancellationToken.None: the originating request token may already be cancelled
            // (client disconnected), but the cleanup write must complete regardless.
            await SafelyCancelOrphanedDispatchedWorkItemAsync(lifecycle, workItemId, "Dispatch lifecycle threw: " + ex.Message);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!dispatched)
        {
            // ExecuteDispatchLifecycleAsync returned without dispatching — PVC race (another replica
            // claimed the same PVC under the lock), or K8s Job creation failed (lifecycle already
            // transitioned the WorkItem to Failed in that case).
            // Clean up the Dispatched item so it doesn't linger: if the lifecycle didn't transition
            // it, cancel it here.
            // DispatchLoop no longer exists to drain orphaned items on the live dispatch path.
            Log.Warning("DispatchWorkItem: lifecycle did not dispatch WorkItem {WorkItemId} (PVC race or K8s failure) — returning 503",
                workItemId);
            await SafelyCancelOrphanedDispatchedWorkItemAsync(lifecycle, workItemId, "Dispatch did not complete (PVC race or K8s failure)");
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Ok(workItemId);
    }

    /// <summary>
    /// Loads project secrets for a work item if a project is configured.
    /// Shared by <see cref="DispatchPendingWorkItem"/> and <see cref="DispatchWorkItem"/> to
    /// eliminate the duplicated prepareVariant lambda body.
    /// </summary>
    private static async Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)> PrepareDispatchVariantAsync(
        PipelineDbContext db,
        WorkItemEntity workItem,
        CancellationToken ct)
    {
        Dictionary<string, string>? projectSecrets = null;
        if (workItem.ProjectId.HasValue)
            projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(
                db, workItem.ProjectId.Value.ToString(), ct);
        return (shouldContinue: true, projectSecrets);
    }

    /// <summary>
    /// Attempts to transition an orphaned Dispatched work item to Cancelled/Failed.
    /// Called when <c>DispatchWorkItem</c> returns 503 and the item may still be in <c>Dispatched</c>
    /// state with no running K8s Job (e.g. PVC race where the lifecycle returned early without
    /// cleaning up, or an unhandled exception during the dispatch lifecycle).
    /// If the lifecycle already moved the item to <c>Failed</c>, the transition is a no-op.
    /// Uses <see cref="CancellationToken.None"/> so the cleanup always completes regardless of
    /// the originating request's lifetime (the request token may already be cancelled if the
    /// client disconnected before the lifecycle completed).
    /// Swallows all exceptions — this is a best-effort cleanup that must not mask the 503.
    /// </summary>
    private static async Task SafelyCancelOrphanedDispatchedWorkItemAsync(
        DispatchLifecycleService lifecycle,
        Guid workItemId,
        string reason)
    {
        try
        {
            // FailWorkItemAsync uses WorkItemTransitionService.TransitionAsync which is a no-op
            // (returns false) when the item is not in Dispatched state, so this is safe to call
            // even if the lifecycle already transitioned the item to Failed.
            await lifecycle.FailWorkItemAsync(workItemId, reason, CancellationToken.None);
            Log.Information("DispatchWorkItem: cancelled orphaned Dispatched WorkItem {WorkItemId} after 503 response", workItemId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DispatchWorkItem: failed to cancel orphaned Dispatched WorkItem {WorkItemId} — item may linger in Dispatched state", workItemId);
        }
    }

    // ── GET /pending ──────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/pending
    /// Returns Pending work items excluding Consolidation task type, ordered by CreatedAt ASC.
    /// Query param: maxResults (default 50).
    /// Includes display fields (IssueTitle, InitiatedBy, ProjectName, ProjectId) extracted from
    /// the Payload JSONB column for the Agent Monitoring Job Queue UI.
    /// </summary>
    internal static async Task<IResult> GetPendingWorkItems(
        IDbContextFactory<PipelineDbContext> dbFactory,
        int maxResults = 50,
        string? projectId = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Phase 1: SQL projection — include Payload and ProjectId alongside the 7 scalar fields.
        // Payload is fetched here so we can extract display fields in-memory (Phase 2).
        var pending = db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending
                     && w.TaskType != WorkItemTaskType.Consolidation);
        // Optional project scope. WorkItem.ProjectId is a uuid column while the switcher passes the
        // project's id as a string (PipelineProject.Id is a Guid-string), so parse before comparing.
        if (!string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var scopeProjectId))
            pending = pending.Where(w => w.ProjectId == scopeProjectId);

        var raw = await pending
            .OrderByDescending(w => w.PriorityWeight)
            .ThenBy(w => w.CreatedAt)
            .Take(maxResults)
            .Select(w => new
            {
                w.Id,
                w.IssueIdentifier,
                w.IssueProviderConfigId,
                w.TaskType,
                w.CreatedAt,
                w.AgentSelector,
                w.RetryCount,
                w.Payload,
                w.ProjectId,
                w.TimeoutSeconds,
                w.PriorityWeight,
                w.TraceParent
            })
            .ToListAsync(ct);

        // Phase 2: in-memory deserialization to extract display fields from Payload.
        // Uses PipelineJsonOptions.Lenient (PropertyNameCaseInsensitive=true) for robustness against
        // payloads written by older serializer configs or with PascalCase keys.
        // A malformed payload produces null display fields rather than a 500 — same defensive pattern
        // used in GetAssignment and PostLabelSwap.
        var items = raw.Select(w =>
        {
            JobDistributionRequest? req = null;
            if (w.Payload is not null)
            {
                try
                {
                    req = JsonSerializer.Deserialize<JobDistributionRequest>(w.Payload, PipelineJsonOptions.Lenient);
                }
                catch (JsonException)
                {
                    // Corrupt or legacy payload — fall back to null display fields for this row.
                }
            }
            return new PendingWorkItemDto
            {
                Id = w.Id,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType,
                CreatedAt = w.CreatedAt,
                AgentSelector = w.AgentSelector,
                RetryCount = w.RetryCount,
                TimeoutSeconds = w.TimeoutSeconds,
                PriorityWeight = w.PriorityWeight,
                IssueTitle = req?.IssueDetail?.Title,
                InitiatedBy = req?.InitiatedBy,
                ProjectName = req?.ProjectName,
                ProjectId = w.ProjectId,
                TraceParent = w.TraceParent
            };
        }).ToList();

        return TypedResults.Ok((IReadOnlyList<PendingWorkItemDto>)items);
    }

    // ── POST /{id}/claim ──────────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/claim
    /// Atomic Pending → Dispatched compare-and-swap. Uses TransitionIfAsync (NOT TransitionAsync).
    /// 200 WorkItemClaimResponse, 409 Conflict (already claimed), 404 not found.
    /// <para>
    /// Also updates the in-memory <see cref="PipelineRun.StartedAtOffset"/> to
    /// <see cref="ClaimWorkItemRequest.DispatchedAt"/> so the UI ELAPSED column reflects actual
    /// run time rather than queue-wait + run time (issue #2106 / BUG-14 K8s path).
    /// Uses ResetStartedAt + ReplaceRun so the mutation is persisted back to Redis in
    /// distributed deployments — ResetStartedAt alone is insufficient because
    /// DistributedRunService.GetRun deserialises a fresh copy on every call.
    /// </para>
    /// </summary>
    internal static async Task<IResult> ClaimWorkItem(
        Guid id,
        [FromBody] ClaimWorkItemRequest request,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        IConfiguration configuration,
        CancellationToken ct)
    {
        string? payloadJson = null;

        var success = await transitionService.TransitionIfAsync(
            id,
            expectedCurrent: WorkItemStatus.Pending,
            target: WorkItemStatus.Dispatched,
            mutate: entity =>
            {
                entity.AssignedAgentId = request.AssignedAgentId;
                entity.DispatchedAt = request.DispatchedAt;
                if (request.K8sJobName is not null)
                    entity.K8sJobName = request.K8sJobName;
                // KiroPvcName is set by the Job Controller for kiro agent dispatches.
                // Written here so QueryAvailablePvcsAsync can compute accurate PVC availability
                // from the DB without querying live K8s Jobs (issue #2338).
                if (request.KiroPvcName is not null)
                    entity.ClaimedPvcName = request.KiroPvcName;
                payloadJson = entity.Payload;
            },
            ct: ct);

        if (!success)
        {
            // Distinguish 404 from 409
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var exists = await db.WorkItems.AnyAsync(w => w.Id == id, ct);
            if (!exists)
                return TypedResults.NotFound();

            return TypedResults.Conflict("Work item is not in Pending state or was already claimed.");
        }

        // Update the in-memory PipelineRun's StartedAtOffset to the actual dispatch time.
        // This fixes the ELAPSED column in the UI, which was showing queue-wait + run time
        // instead of just run time (issue #2106 / BUG-14 recurrence in the K8s API path).
        //
        // ReplaceRun is required (not just ResetStartedAt) because DistributedRunService.GetRun
        // returns a freshly deserialised copy — without ReplaceRun the mutation is discarded and
        // the Redis hash retains the original enqueue-time StartedAtOffset.
        //
        // Null-safe: run is null when the API pod restarted between CreateWorkItem and ClaimWorkItem
        // (no in-memory run exists). The DB-backed fallback (PostgresActiveRunQueryService) already
        // uses DispatchedAt ?? CreatedAt for elapsed calculation, so that path is unaffected.
        //
        // Default guard: DispatchedAt is 'required DateTimeOffset' (non-nullable); a zero-epoch
        // default value would produce a nonsensical StartedAtOffset, so skip the update in that case.
        var run = runService.GetRun(new RunId(id.ToString()));
        if (run is not null && request.DispatchedAt != default)
        {
            run.ResetStartedAt(request.DispatchedAt);
            runService.ReplaceRun(run);
        }

        // Derive RunId from workItemId if not set in payload
        // (deterministic: reuses WorkItemId bytes to form a stable GUID)
        var runId = DeriveRunId(id);

        // Read OrchestratorUrl from configuration
        var orchestratorUrl =
            configuration.GetValue<string>("WorkDistribution:OrchestratorUrl")
            ?? configuration.GetValue<string>("OrchestratorUrl")
            ?? "";

        var response = new WorkItemClaimResponse
        {
            WorkItemId = id,
            RunId = runId,
            PayloadJson = payloadJson ?? "",
            OrchestratorUrl = orchestratorUrl
        };

        return TypedResults.Ok(response);
    }

    // ── POST /{id}/requeue ─────────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/requeue
    /// Transitions Failed/Cancelled/Dispatched → Pending, incrementing RetryCount.
    /// Dispatched→Pending covers the case where Job creation fails after a successful claim
    /// (ProcessItemAsync calls SafeRequeueAsync while the item is still in Dispatched state).
    /// 200, 409 Conflict (wrong state), 404.
    /// </summary>
    internal static async Task<IResult> RequeueWorkItem(
        Guid id,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        // Try Failed → Pending
        var succeededFromFailed = await transitionService.TransitionIfAsync(
            id,
            expectedCurrent: WorkItemStatus.Failed,
            target: WorkItemStatus.Pending,
            mutate: entity =>
            {
                entity.RetryCount++;
                entity.DispatchedAt = null;
                entity.AssignedAgentId = null;
            },
            ct: ct);

        if (succeededFromFailed)
            return TypedResults.Ok();

        // Try Cancelled → Pending
        var succeededFromCancelled = await transitionService.TransitionIfAsync(
            id,
            expectedCurrent: WorkItemStatus.Cancelled,
            target: WorkItemStatus.Pending,
            mutate: entity =>
            {
                entity.RetryCount++;
                entity.DispatchedAt = null;
                entity.AssignedAgentId = null;
            },
            ct: ct);

        if (succeededFromCancelled)
            return TypedResults.Ok();

        // Try Dispatched → Pending — handles Job creation failures where ClaimAsync succeeded
        // but the K8s Job could not be created (API server unreachable, invalid spec, no PVC).
        // Without this, the item stays stuck in Dispatched until EnforceDispatchedTimeoutAsync
        // marks it Failed (losing the retry rather than re-queuing it).
        var succeededFromDispatched = await transitionService.TransitionIfAsync(
            id,
            expectedCurrent: WorkItemStatus.Dispatched,
            target: WorkItemStatus.Pending,
            mutate: entity =>
            {
                entity.RetryCount++;
                entity.DispatchedAt = null;
                entity.AssignedAgentId = null;
                entity.K8sJobName = null;
            },
            ct: ct);

        if (succeededFromDispatched)
            return TypedResults.Ok();

        // Check existence to differentiate 404 from 409
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.Status })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        // Item exists but is in wrong state (e.g. Pending/Running/Succeeded)
        return TypedResults.Conflict($"Cannot requeue work item in status '{item.Status}'.");
    }

    // ── GET /{id}/retry-count ──────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/retry-count
    /// Returns { "retryCount": int }. 200 or 404.
    /// </summary>
    internal static async Task<IResult> GetRetryCount(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.RetryCount })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new { retryCount = item.RetryCount });
    }

    // ── GET /staleness ─────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/staleness?issueIdentifier=...&amp;issueProviderConfigId=...&amp;since=...
    /// Returns WorkItemStalenessResult.
    /// </summary>
    internal static async Task<IResult> GetStaleness(
        string issueIdentifier,
        string issueProviderConfigId,
        DateTimeOffset since,
        WorkItemTransitionService transitionService,
        CancellationToken ct)
    {
        var issueId = new IssueIdentifier(issueIdentifier);
        var providerConfigId = new ProviderConfigId(issueProviderConfigId);

        var hasAgentError = await transitionService.HasAgentErrorSinceAsync(issueId, providerConfigId, since, ct);
        var lastSuccess = await transitionService.GetLastSuccessfulCompletionAsync(issueId, providerConfigId, ct);

        return TypedResults.Ok(new WorkItemStalenessResult
        {
            HasAgentErrorSince = hasAgentError,
            LastSuccessfulCompletion = lastSuccess
        });
    }

    // ── GET /active ───────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/active?olderThanSeconds=N
    /// Returns WorkItems in Dispatched or Running status with DispatchedAt &lt; now - N seconds.
    /// Used by ReconciliationService for timeout enforcement and short-circuit Dispatched sweep.
    /// </summary>
    internal static async Task<IResult> GetActiveWorkItems(
        int olderThanSeconds,
        IDbContextFactory<PipelineDbContext> dbFactory,
        // TODO: The nullable optional DI service creates a silent-degradation pattern — if
        // IOrchestratorRunService is ever accidentally unregistered, the endpoint silently returns
        // no CurrentStep enrichment instead of failing at startup. Tests call this handler directly
        // with an explicit null argument so they do not rely on DI optional resolution; in
        // production the service is always registered as a singleton. Consider switching to a
        // non-nullable required [FromServices] parameter once the test callability story is clear.
        IOrchestratorRunService? runService = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-olderThanSeconds);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = db.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && (w.DispatchedAt < cutoff
                         // Fallback for items where DispatchedAt is null (e.g., claim write failed):
                         // use CreatedAt so they are not permanently invisible to timeout enforcement.
                         // 1C-001: NULL < cutoff evaluates to NULL (falsy) in SQL, excluding these rows.
                         || (w.DispatchedAt == null && w.CreatedAt < cutoff)));
        // Optional project scope (not passed by reconciliation). ProjectId is a uuid column; parse the
        // switcher's Guid-string id before comparing.
        if (!string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var scopeProjectId))
            active = active.Where(w => w.ProjectId == scopeProjectId);

        var items = await active
            .Select(w => new
            {
                w.Id,
                w.Status,
                w.DispatchedAt,
                w.AgentSelector,
                w.IssueIdentifier,
                w.K8sJobName,
                w.TimeoutSeconds,
                w.Payload
            })
            .ToListAsync(ct);

        // Phase 2: in-memory deserialization to extract IssueTitle and InitiatedBy from Payload.
        // Same defensive pattern as GetPendingWorkItems — a malformed or absent payload
        // produces null display fields rather than a 500.
        var dtos = items.Select(w =>
        {
            string? issueTitle = null;
            string? initiatedBy = null;
            if (w.Payload is not null)
            {
                try
                {
                    var req = JsonSerializer.Deserialize<JobDistributionRequest>(w.Payload, PipelineJsonOptions.Lenient);
                    issueTitle = req?.IssueDetail?.Title;
                    initiatedBy = req?.InitiatedBy;
                }
                catch (JsonException)
                {
                    // Corrupt or legacy payload — leave display fields null.
                }
            }
            return new ActiveWorkItemDto
            {
                Id = w.Id,
                Status = w.Status,
                DispatchedAt = w.DispatchedAt,
                AgentSelector = w.AgentSelector,
                IssueIdentifier = w.IssueIdentifier,
                K8sJobName = w.K8sJobName,
                TimeoutSeconds = w.TimeoutSeconds,
                IssueTitle = issueTitle,
                InitiatedBy = initiatedBy
            };
            // TODO [WARNING]: ct is available and used in the SQL phase (ToListAsync(ct)) but is not
            // propagated to this in-memory LINQ loop. Under normal payload sizes this is harmless because
            // the deserialization is synchronous and fast. If payload sizes grow significantly, consider
            // adding a cancellation check (ct.ThrowIfCancellationRequested()) inside the loop body.
        }).ToList();

        // Enrich with live pipeline step from the in-memory run service when available.
        // runService may be null in test scenarios that construct the handler directly without DI.
        if (runService is not null)
        {
            for (var i = 0; i < dtos.Count; i++)
            {
                // TODO: The explicit cast (RunId) calls ArgumentException.ThrowIfNullOrEmpty internally.
                // Guid.ToString() is always non-null/non-empty, so this is safe in practice, but if
                // ActiveWorkItemDto.Id ever becomes nullable (Guid?) the cast would throw instead of
                // skipping enrichment. Consider using new RunId(dtos[i].Id.ToString()) for clarity.
                var liveRun = runService.GetRun((RunId)dtos[i].Id.ToString());
                if (liveRun is not null)
                    dtos[i] = dtos[i] with { CurrentStep = liveRun.CurrentStep };
            }
        }

        return TypedResults.Ok((IReadOnlyList<ActiveWorkItemDto>)dtos);
    }

    // ── POST /{id}/label-swap ─────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/label-swap
    /// Body: { "label": string } — the label field is kept for wire compatibility but is NOT
    /// used to drive behavior. The handler always swaps to agent:in-progress.
    /// Returns 200, 404 if WorkItem not found.
    ///
    /// <para>
    /// The target follows the work item's task type. A review's identifier is a pull request
    /// number, not an issue number, so labelling it as an issue puts the in-progress marker on
    /// whatever issue happens to share that number. This handler used to pass
    /// <c>LabelTargetKind.Issue</c> unconditionally — "work items are always issue-origin at
    /// dispatch time", which reviews are not — and it disagreed with the completion path, which
    /// labels the pull request. GitHub hides the disagreement, since its issues API accepts a PR
    /// number; a provider that keeps the two apart does not.
    /// </para>
    /// </summary>
    internal static async Task<IResult> PostLabelSwap(
        Guid id,
        [FromBody] LabelSwapRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILabelSwapService? labelSwapService,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.IssueProviderConfigId, w.IssueIdentifier, w.TaskType, w.Payload })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        if (labelSwapService is not null)
        {
            var isReview = item.TaskType == WorkItemTaskType.Review;
            var targetKind = isReview ? LabelTargetKind.PullRequest : LabelTargetKind.Issue;

            // A PR label swap must go through the *repository* provider — SwapPrLabelAsync resolves
            // the config as ProviderKind.Repository — but the work item only stores its issue
            // provider config as a column. For a review the repo provider lives in the serialized
            // JobDistributionRequest payload (same source the assignment endpoint reads), so pull
            // it from there. Passing the issue config id would make the repo lookup miss and the
            // swap silently no-op, which is why review PRs never got the in-progress marker.
            var providerConfigIdValue = item.IssueProviderConfigId;
            if (isReview && item.Payload is not null)
            {
                var payload = JsonSerializer.Deserialize<JobDistributionRequest>(
                    item.Payload, PipelineJsonOptions.Default);
                if (!string.IsNullOrEmpty(payload?.RepoProviderConfigId))
                    providerConfigIdValue = payload.RepoProviderConfigId;
            }

            await labelSwapService.SwapLabelWithRetryAsync(
                id,
                new ProviderConfigId(providerConfigIdValue),
                new IssueIdentifier(item.IssueIdentifier),
                targetKind,
                ct);
        }

        return TypedResults.Ok();
    }

    // ── POST /{id}/last-progress ──────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/last-progress
    /// Body: { "timestamp": DateTimeOffset }
    /// Updates the LastProgressAt field. Returns 200 or 404.
    /// NOTE: LastProgressAt column already exists via migration AddLastProgressAtToWorkItems.
    /// No EF migration is needed.
    /// </summary>
    internal static async Task<IResult> PostLastProgress(
        Guid id,
        [FromBody] LastProgressRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems
            .Where(w => w.Id == id)
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        item.LastProgressAt = request.Timestamp;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok();
    }

    // ── GET /{id}/k8s-job-name ────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/k8s-job-name
    /// Returns the K8s Job name associated with a WorkItem.
    /// Used by KubernetesJobCleanup to cancel the K8s Job when an issue is cancelled.
    /// 200 with { jobName: string } or 404 if not found / no job name set.
    /// </summary>
    internal static async Task<IResult> GetK8sJobName(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobName = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => w.K8sJobName)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(jobName))
            return TypedResults.NotFound();

        return TypedResults.Ok(new { jobName });
    }

    // ── GET /{id}/status ──────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/status
    /// Returns the current <see cref="WorkItemStatus"/> of a WorkItem.
    /// Used by KubernetesWorkDistributor.GetJobStatusAsync to check run status.
    /// 200 with { status: string }, 404 if not found.
    /// </summary>
    internal static async Task<IResult> GetWorkItemStatus(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var status = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => (WorkItemStatus?)w.Status)
            .FirstOrDefaultAsync(ct);

        if (status is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new { status });
    }

    // ── GET /is-distributed ───────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/is-distributed?issueIdentifier=...&amp;issueProviderConfigId=...
    /// Returns true when a non-terminal WorkItem exists for this issue, OR when a WorkItem
    /// was recently terminated (within <see cref="PipelineConstants.DefaultRestartDedupCooldown"/>).
    /// Used by KubernetesWorkDistributor.IsIssueDistributedAsync for dispatch deduplication.
    /// 200 with { isDistributed: bool }.
    /// </summary>
    internal static async Task<IResult> GetIsDistributed(
        string issueIdentifier,
        string issueProviderConfigId,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;

        var hasActive = await db.WorkItems
            .AsNoTracking()
            .AnyAsync(w =>
                w.IssueIdentifier == issueIdentifier &&
                w.IssueProviderConfigId == issueProviderConfigId &&
                activeStatuses.Contains(w.Status), ct);

        if (hasActive)
            return TypedResults.Ok(new { isDistributed = true });

        var recentTerminalCutoff = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;
        var hasRecentTerminal = await db.WorkItems
            .AsNoTracking()
            .AnyAsync(w =>
                w.IssueIdentifier == issueIdentifier &&
                w.IssueProviderConfigId == issueProviderConfigId &&
                !activeStatuses.Contains(w.Status) &&
                w.CompletedAt != null &&
                w.CompletedAt >= recentTerminalCutoff, ct);

        return TypedResults.Ok(new { isDistributed = hasRecentTerminal });
    }

    // ── GET /active-identifiers ───────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/active-identifiers
    /// Returns the set of (IssueIdentifier, IssueProviderConfigId) pairs that have active
    /// (non-terminal) WorkItems OR were recently terminated.
    /// Used by KubernetesWorkDistributor.GetActiveIssueIdentifiersAsync for dispatch deduplication.
    /// 200 with array of { issueIdentifier, issueProviderConfigId }.
    /// </summary>
    internal static async Task<IResult> GetActiveIdentifiers(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
        var recentTerminalCutoff = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;

        var activePairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => activeStatuses.Contains(w.Status))
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        var recentTerminalPairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => !activeStatuses.Contains(w.Status) &&
                        w.CompletedAt != null &&
                        w.CompletedAt >= recentTerminalCutoff)
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        var result = activePairs
            .Concat(recentTerminalPairs)
            .Select(p => new ActiveIdentifierDto(p.IssueIdentifier, p.IssueProviderConfigId))
            .Distinct()
            .ToList();

        return TypedResults.Ok((IReadOnlyList<ActiveIdentifierDto>)result);
    }

    /// <summary>
    /// DTO returned by <see cref="GetActiveIdentifiers"/>. Named type eliminates the
    /// S1944 suspicious-cast warning and preserves field-name stability on the wire.
    /// </summary>
    internal sealed record ActiveIdentifierDto(string IssueIdentifier, string IssueProviderConfigId);

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

        if (request.Status == WorkItemStatus.Failed)
        {
            // TODO: This bare Enum.TryParse has no Enum.IsDefined guard (unlike the telemetry path
            // fixed in issue #2341). A numeric string like "99" will parse to an undefined FailureReason
            // value and be persisted to the database. Add an Enum.IsDefined check here so that only
            // named members are written to entity.FailureReason.
            if (request.FailureReason is not null
                && Enum.TryParse<FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason))
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
            Serilog.Log.ForContext("SourceContext", nameof(WorkItemEndpoints))
                .Warning(ex, "Failed to emit terminal status telemetry for WorkItem {Id}", id);
        }
    }

    internal static bool IsUniqueViolation(Exception ex)
    {
        // Postgres path: DbUpdateException wrapping a PostgresException with SQLSTATE 23505
        if (ex is DbUpdateException { InnerException: PostgresException pg })
            return pg.SqlState == "23505";

        // Postgres fallback (non-Npgsql drivers) and EF InMemory path.
        // EF InMemory throws ArgumentException("An item with the same key has already been added")
        // directly — it is NOT wrapped in DbUpdateException — so we must check the top-level
        // message as well as the inner exception message.
        // Note: the EF InMemory phrase is an implementation detail and may change across EF Core versions.
        var message = ex.Message ?? "";
        var innerMessage = ex.InnerException?.Message ?? "";
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            // EF InMemory exact phrase
            || message.Contains("An item with the same key has already been added", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Derives a deterministic RunId string from the work item GUID.
    /// Checks the payload for a pre-assigned RunId first, then falls back to workItemId-based derivation.
    /// </summary>
    private static string DeriveRunId(Guid workItemId)
    {
        // Use workItemId as the run ID (bytes rearranged to produce a v4-like GUID)
        // This is deterministic and stable across retries.
        return workItemId.ToString();
    }

    // ── GET /api/work-items/counts-by-status ─────────────────────────────────

    /// <summary>
    /// Returns work item counts grouped by (Status, AgentSelector).
    /// Called by the Scheduler's WorkItemCountsPoller to feed Prometheus gauges.
    /// </summary>
    internal static async Task<IResult> GetCountsByStatus(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.WorkItems
            .GroupBy(w => new { w.Status, w.AgentSelector })
            .Select(g => new
            {
                Status = g.Key.Status.ToString(),
                AgentSelector = g.Key.AgentSelector,
                Count = (long)g.Count()
            })
            .ToListAsync(ct);

        return Results.Ok(counts.Select(c =>
            new CodingAgent.Api.Client.WorkItemCountDto(c.Status, c.AgentSelector, c.Count))
            .ToArray());
    }

    // ── POST /{id}/priority ───────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/priority
    /// Body: { "priorityWeight": int }
    /// Sets <see cref="WorkItemEntity.PriorityWeight"/> on a Pending work item.
    /// Returns 200 on success.
    /// Returns 400 if <c>priorityWeight</c> is outside [0, 1000].
    /// Returns 409 if the item is not in Pending status.
    /// Returns 404 if the item does not exist.
    /// </summary>
    internal static async Task<IResult> PostPriorityWeight(
        Guid id,
        [FromBody] PriorityWeightRequest request,
        WorkItemTransitionService transitionService,
        CancellationToken ct)
    {
        if (request.PriorityWeight is null)
            return TypedResults.BadRequest("priorityWeight is required.");

        if (request.PriorityWeight < 0 || request.PriorityWeight > 1000)
            return TypedResults.BadRequest("priorityWeight must be between 0 and 1000 (inclusive).");

        var result = await transitionService.UpdatePriorityWeightAsync(id, request.PriorityWeight.Value, ct);

        return result switch
        {
            Infrastructure.Persistence.Services.UpdatePriorityWeightResult.Success => TypedResults.Ok(),
            Infrastructure.Persistence.Services.UpdatePriorityWeightResult.NotPending =>
                TypedResults.Conflict("Cannot update PriorityWeight: work item is not in Pending status."),
            Infrastructure.Persistence.Services.UpdatePriorityWeightResult.ConcurrencyConflict =>
                TypedResults.Conflict("Cannot update PriorityWeight: concurrent update conflict, please retry."),
            // TODO: [WARNING] The default arm silently maps any future UpdatePriorityWeightResult values
            // to 404 NotFound. If a new result code is added to the enum, the compiler will not warn
            // that it is unhandled here. Consider adding an explicit case for UpdatePriorityWeightResult.NotFound
            // and replacing the default arm with a throw (or a 500 response) to catch unhandled cases
            // at compile time. The current behavior is correct for all existing values.
            _ => TypedResults.NotFound()
        };
    }
}

/// <summary>
/// Request body for POST /api/work-items/{id}/priority.
/// </summary>
public sealed class PriorityWeightRequest
{
    /// <summary>
    /// Dispatch priority weight. Must be between 0 and 1000 (inclusive). Required.
    /// </summary>
    public int? PriorityWeight { get; init; }
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
}

/// <summary>
/// Request body for POST /api/work-items/{id}/label-swap.
/// The <see cref="Label"/> field is kept for wire compatibility but is NOT used to drive behavior.
/// The handler always calls <see cref="ILabelSwapService.SwapLabelWithRetryAsync"/> with
/// <see cref="LabelTargetKind.Issue"/> — the string value is ignored.
/// </summary>
public sealed class LabelSwapRequest
{
    public string Label { get; init; } = "";
}

/// <summary>
/// Request body for POST /api/work-items/{id}/last-progress.
/// </summary>
public sealed class LastProgressRequest
{
    public required DateTimeOffset Timestamp { get; init; }
}
