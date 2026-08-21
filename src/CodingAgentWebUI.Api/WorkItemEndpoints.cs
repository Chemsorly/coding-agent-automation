using System.Security.Claims;
using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

namespace CodingAgentWebUI.Api;

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
                   HttpContext httpContext,
                   CancellationToken ct) =>
                await AuthorizeAgentForWorkItemAsync(httpContext, id, dbFactory, ct)
                    ?? await GetAssignment(id, dbFactory, projectStore));

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
        group.MapPost("/{id:guid}/requeue", RequeueWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/{id:guid}/retry-count", GetRetryCount).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/staleness", GetStaleness).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/label-swap", PostLabelSwap).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/last-progress", PostLastProgress).RequireAuthorization(ApiAuthPolicies.Operator);
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
    /// </summary>
    internal static async Task<IResult> GetAssignment(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IProjectStore projectStore,
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

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(id, request);

        // Inject project secrets at delivery time (not serialized in payload for security)
        if (!string.IsNullOrEmpty(request.ProjectId))
        {
            var project = await projectStore.GetProjectByIdAsync(request.ProjectId, ct);
            if (project?.Secrets is { Count: > 0 })
                message = message with { ProjectSecrets = project.Secrets };
        }

        return TypedResults.Ok(message);
    }

    // ── POST /{id}/status — mirror of monolith ────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/status
    /// Validates transition via WorkItemTransitionService, updates in-memory state.
    /// 200, 400 (invalid transition), or 404.
    /// </summary>
    internal static async Task<IResult> PostStatus(
        Guid id,
        WorkItemStatusRequest request,
        WorkItemTransitionService transitionService,
        IOrchestratorRunService runService,
        IRunLifecycleManager runLifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null,
        CancellationToken ct = default)
    {
        var success = await transitionService.TransitionAsync(
            id, request.Status,
            mutate: entity => ApplyStatusMutation(entity, request),
            ct: ct);

        if (!success)
        {
            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var exists = await db.WorkItems.AnyAsync(w => w.Id == id, ct);
                if (!exists)
                    return TypedResults.NotFound();
            }

            return TypedResults.BadRequest("Invalid status transition");
        }

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
                CodingAgentWebUI.Pipeline.Models.FailureReason.InfrastructureFailure);
        }
        else if (request.Status == WorkItemStatus.Cancelled)
        {
            await runLifecycleManager.CancelRunAsync(new RunId(id.ToString()), ct);
        }

        // Emit telemetry for terminal transitions — fire-and-forget: enrichment query must
        // not block the agent's 200 response, and a slow/failed DB read must not surface as a 500.
        if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            _ = EmitTerminalStatusTelemetryAsync(id, request, dbFactory, ct);

        return TypedResults.Ok();
    }

    // ── POST / — create WorkItem ───────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items
    /// Creates a new WorkItem with Status=Pending from a JobDistributionRequest.
    /// Also materialises an in-memory <see cref="PipelineRun"/> in <see cref="IOrchestratorRunService"/>
    /// so the UI can show the run immediately (Option A, Req 1a.1).
    /// Returns 201 + new GUID. Maps Postgres 23505 unique violation to 409 Conflict.
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
        var payloadJson = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Pending,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector ?? "",
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Postgres 23505: unique index on (IssueIdentifier, IssueProviderConfigId) for non-terminal statuses
            return TypedResults.Conflict("A live work item already exists for this issue.");
        }

        // Materialise in-memory PipelineRun in the API's IOrchestratorRunService so the UI
        // can subscribe to hub events and display the run immediately (Req 1a.1 Option A).
        // WorkItem.Id == PipelineRun.RunId for deterministic hub-group routing.
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
        runService.AddRun(run);

        return TypedResults.Created($"/api/work-items/{workItemId}", workItemId);
    }

    // ── GET /pending ──────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/pending
    /// Returns Pending work items excluding Consolidation task type, ordered by CreatedAt ASC.
    /// Query param: maxResults (default 50).
    /// </summary>
    internal static async Task<IResult> GetPendingWorkItems(
        IDbContextFactory<PipelineDbContext> dbFactory,
        int maxResults = 50,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending
                     && w.TaskType != WorkItemTaskType.Consolidation)
            .OrderBy(w => w.CreatedAt)
            .Take(maxResults)
            .Select(w => new PendingWorkItemDto
            {
                Id = w.Id,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType,
                CreatedAt = w.CreatedAt,
                AgentSelector = w.AgentSelector,
                RetryCount = w.RetryCount
            })
            .ToListAsync(ct);

        return TypedResults.Ok((IReadOnlyList<PendingWorkItemDto>)items);
    }

    // ── POST /{id}/claim ──────────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/claim
    /// Atomic Pending → Dispatched compare-and-swap. Uses TransitionIfAsync (NOT TransitionAsync).
    /// 200 WorkItemClaimResponse, 409 Conflict (already claimed), 404 not found.
    /// </summary>
    internal static async Task<IResult> ClaimWorkItem(
        Guid id,
        [FromBody] ClaimWorkItemRequest request,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
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
    /// Transitions Failed/Cancelled → Pending, incrementing RetryCount.
    /// 200, 409 Conflict (wrong state), 404.
    /// </summary>
    internal static async Task<IResult> RequeueWorkItem(
        Guid id,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        // Try both Failed and Cancelled → Pending (use TransitionIfAsync for each)
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

        // Check existence to differentiate 404 from 409
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.Status })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        // Item exists but is in wrong state (e.g. Pending/Dispatched/Running/Succeeded)
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
        CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-olderThanSeconds);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await db.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && w.DispatchedAt < cutoff)
            .Select(w => new ActiveWorkItemDto
            {
                Id = w.Id,
                Status = w.Status,
                DispatchedAt = w.DispatchedAt,
                AgentSelector = w.AgentSelector,
                IssueIdentifier = w.IssueIdentifier
            })
            .ToListAsync(ct);

        return TypedResults.Ok((IReadOnlyList<ActiveWorkItemDto>)items);
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

            WorkDistributionTelemetry.LogTerminalStatus(
                id, request.Status, duration, request.AgentId, failureReason: null);
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext("SourceContext", nameof(WorkItemEndpoints))
                .Warning(ex, "Failed to emit terminal status telemetry for WorkItem {Id}", id);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        if (inner is PostgresException pg)
            return pg.SqlState == "23505";

        return ex.InnerException?.Message?.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message?.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true;
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
