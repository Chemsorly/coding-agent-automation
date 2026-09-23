using System.Security.Claims;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration.Dispatch;
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
                   [FromServices] WorkItemStatusTransitionService statusTransitionService,
                   // TODO: [WARNING] dbFactory is injected here exclusively for AuthorizeAgentForWorkItemAsync.
                   // It is NOT forwarded to PostStatus — WorkItemStatusTransitionService holds its own
                   // IDbContextFactory reference injected at DI registration time. If the singleton's
                   // registration ever loses its factory reference, this route-level resolve will NOT act
                   // as a safety net (it is discarded). A comment or rename would clarify the intent.
                   [FromServices] IDbContextFactory<PipelineDbContext> dbFactory,
                   HttpContext httpContext,
                   CancellationToken ct) =>
                await AuthorizeAgentForWorkItemAsync(httpContext, id, dbFactory, ct)
                    ?? await PostStatus(id, request, statusTransitionService, ct))
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

        if (!WorkItemPayload.TryDeserialize(item.Payload, out var requestNullable))
            return TypedResults.NotFound();

        // TryDeserialize returned true, so requestNullable is guaranteed non-null here.
        var request = requestNullable!;

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
        // Secret injection delegates to AssignmentEnricher.InjectProjectSecretsAsync (issue #2914),
        // which uses ConsolidationTemplateResolver for the consolidation fallback path rather than
        // a reimplemented ownership-resolution loop.
        // projectStore is passed as a fallback so test subclasses of AssignmentEnricher constructed
        // via the protected logger-only constructor (which set _projectStore = null!) still work.
        // TODO: [WARNING] InjectProjectSecretsAsync is now called unconditionally for all GetAssignment
        // requests, including old-schema requests where request.PayloadSchemaVersion == null. In the
        // prior code, secret injection was inside the isNewSchema branch only. Old-schema callers that
        // log or persist the full response message now receive live ProjectSecrets where they did not
        // before, widening the secret exposure surface even though the endpoint already requires Operator
        // auth. Gate this call on request.PayloadSchemaVersion == 1 (new-schema path only), or explicitly
        // document the intentional behavior change so operators are aware secrets are now injected on all
        // paths. (Security review finding — issue #2914.)
        if (assignmentEnricher is not null)
            message = await assignmentEnricher.InjectProjectSecretsAsync(message, request, ct, projectStore);

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
    /// Builds a minimal <see cref="PipelineProject"/> stub for work items without a project ID.
    /// Prevents null-ref in <see cref="AssignmentEnricher.EnrichAsync"/> which requires a non-null project.
    /// </summary>
    private static PipelineProject BuildMinimalProject(JobDistributionRequest request)
        => new()
        {
            Id = request.ProjectId?.ToString() ?? Guid.Empty.ToString(),
            Name = request.ProjectName ?? string.Empty
        };

    // ── POST /{id}/status ─────────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/status
    /// Thin shim: delegates all orchestration logic to <see cref="WorkItemStatusTransitionService"/>
    /// and maps the outcome to an <c>IResult</c>. Keeps the internal static signature and
    /// <c>awaitTelemetry</c> test seam so that <c>PostStatusIdempotencyTests.cs</c> can continue
    /// calling this method directly via <c>InternalsVisibleTo</c>.
    /// </summary>
    // Suppression: CA1068 — awaitTelemetry is a test-only seam appended after ct intentionally.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Test seam bool appended after ct intentionally")]
    internal static async Task<IResult> PostStatus(
        Guid id,
        WorkItemStatusRequest request,
        WorkItemStatusTransitionService statusTransitionService,
        CancellationToken ct = default,
        bool awaitTelemetry = false)
    {
        var outcome = await statusTransitionService.TransitionAsync(id, request, ct, awaitTelemetry);

        return outcome switch
        {
            StatusTransitionOutcome.Transitioned => TypedResults.Ok(),
            StatusTransitionOutcome.AlreadyAtTarget => TypedResults.NoContent(),
            StatusTransitionOutcome.NotFound => TypedResults.NotFound(),
            StatusTransitionOutcome.Rejected => TypedResults.BadRequest("Invalid status transition"),
            _ => TypedResults.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    // ── Backward-compatibility overload (used by tests that pass the old parameters directly) ──

    /// <summary>
    /// Backward-compatible overload for <c>PostStatusIdempotencyTests.cs</c>, which constructs
    /// <see cref="WorkItemTransitionService"/>, <see cref="IOrchestratorRunService"/>, and
    /// <see cref="IRunLifecycleManager"/> directly. This overload wraps the three dependencies
    /// into a <see cref="WorkItemStatusTransitionService"/> and forwards to the primary overload.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Test seam bool appended after ct intentionally")]
    internal static Task<IResult> PostStatus(
        Guid id,
        WorkItemStatusRequest request,
        WorkItemTransitionService transitionService,
        IOrchestratorRunService runService,
        IRunLifecycleManager runLifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null,
        CancellationToken ct = default,
        bool awaitTelemetry = false)
    {
        // TODO: [WARNING] runService is accepted here only to maintain call-site compatibility with
        // existing tests that pass it by position. It is intentionally discarded — WorkItemStatusTransitionService
        // does not consume IOrchestratorRunService. Callers passing a non-null runService receive no
        // indication that the argument has no effect, which could mask a future intent to use it.
        // If runService is genuinely unused, consider marking the parameter with _ = runService or
        // adding a #pragma warning disable IDE0060 suppression; if it should be used, wire it through.
        _ = runService;
        var svc = new WorkItemStatusTransitionService(transitionService, runLifecycleManager, dbFactory);
        return PostStatus(id, request, svc, ct, awaitTelemetry);
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
