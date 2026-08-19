using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for the Work Item HTTP API.
/// A superset of the monolith's WorkItemEndpoints, adding claim, requeue, retry-count, pending, and staleness.
/// All endpoints require the AgentApiKey authorization policy.
/// </summary>
public static class WorkItemEndpoints
{
    /// <summary>
    /// Maps work item endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-items")
            .RequireAuthorization("AgentApiKey");

        // ── Monolith-mirror endpoints ──────────────────────────────────────
        group.MapGet("/{id:guid}/assignment", GetAssignment);

        group.MapPost("/{id:guid}/status",
            async (Guid id, [FromBody] WorkItemStatusRequest request,
                   [FromServices] WorkItemTransitionService transitionService,
                   [FromServices] IOrchestratorRunService runService,
                   HttpContext httpContext) =>
                await PostStatus(id, request, transitionService, runService,
                    httpContext.RequestServices.GetService<IDbContextFactory<PipelineDbContext>>()))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1_048_576)); // 1 MB limit

        // ── New endpoints ──────────────────────────────────────────────────
        group.MapPost("/", CreateWorkItem);
        group.MapGet("/pending", GetPendingWorkItems);
        group.MapGet("/active", GetActiveWorkItems);
        group.MapPost("/{id:guid}/claim", ClaimWorkItem);
        group.MapPost("/{id:guid}/requeue", RequeueWorkItem);
        group.MapGet("/{id:guid}/retry-count", GetRetryCount);
        group.MapGet("/staleness", GetStaleness);
        group.MapPost("/{id:guid}/label-swap", PostLabelSwap);
        group.MapPost("/{id:guid}/last-progress", PostLastProgress);
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
        IProjectStore projectStore)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.Status, w.Payload })
            .FirstOrDefaultAsync();

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

        var message = DbWorkDistributorBase.BuildJobAssignmentMessage(id, request);

        // Inject project secrets at delivery time (not serialized in payload for security)
        if (!string.IsNullOrEmpty(request.ProjectId))
        {
            var project = await projectStore.GetProjectByIdAsync(request.ProjectId, CancellationToken.None);
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
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
    {
        var success = await transitionService.TransitionAsync(
            id, request.Status,
            mutate: entity => ApplyStatusMutation(entity, request));

        if (!success)
        {
            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var exists = await db.WorkItems.AnyAsync(w => w.Id == id);
                if (!exists)
                    return TypedResults.NotFound();
            }

            return TypedResults.BadRequest("Invalid status transition");
        }

        // Emit telemetry for terminal transitions
        if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            await EmitTerminalStatusTelemetryAsync(id, request, dbFactory);

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
        IOrchestratorRunService runService)
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
            await using var db = await dbFactory.CreateDbContextAsync();
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync();
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
        int maxResults = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
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
            .ToListAsync();

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
    /// used to drive behavior. The handler always calls SwapLabelWithRetryAsync with
    /// LabelTargetKind.Issue (work items are always issue-origin at dispatch time).
    /// Returns 200, 404 if WorkItem not found.
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
            .Select(w => new { w.IssueProviderConfigId, w.IssueIdentifier })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        if (labelSwapService is not null)
        {
            var providerConfigId = new ProviderConfigId(item.IssueProviderConfigId);
            var issueIdentifier = new IssueIdentifier(item.IssueIdentifier);

            // Label field is not used — always swap to agent:in-progress (LabelTargetKind.Issue)
            await labelSwapService.SwapLabelWithRetryAsync(
                id, providerConfigId, issueIdentifier, LabelTargetKind.Issue, ct);
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

    // ── Private helpers ────────────────────────────────────────────────────

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
        IDbContextFactory<PipelineDbContext>? dbFactory)
    {
        TimeSpan? duration = null;
        if (dbFactory is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var item = await db.WorkItems.AsNoTracking()
                .Where(w => w.Id == id)
                .Select(w => new { w.DispatchedAt, w.CompletedAt })
                .FirstOrDefaultAsync();
            if (item?.DispatchedAt is not null && item.CompletedAt is not null)
                duration = item.CompletedAt.Value - item.DispatchedAt.Value;
        }

        WorkDistributionTelemetry.LogTerminalStatus(
            id, request.Status, duration, request.AgentId, failureReason: null);
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
