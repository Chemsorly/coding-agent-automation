using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Background service that drains Pending WorkItems from the DB by assigning them
/// to idle agents via SignalR. Wakes on signal (agent became idle) or periodic sweep.
/// Only active in DB+SignalR mode.
/// </summary>
public sealed class PendingWorkItemDrainService : BackgroundService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ISignalRWorkDistributorAgentResolver _agentResolver;
    private readonly IAgentCommunication _agentComm;
    private readonly IOrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly ILabelService _labelService;
    private readonly IProjectStore? _projectStore;
    private readonly IConsolidationDispatchService? _consolidationDispatcher;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly ILogger<PendingWorkItemDrainService> _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(5);

    public PendingWorkItemDrainService(
        DrainServiceDependencies deps,
        IProjectStore? projectStore = null,
        IConsolidationDispatchService? consolidationDispatcher = null,
        IConsolidationRunStore? consolidationRunStore = null)
    {
        _dbFactory = deps.DbFactory;
        _agentResolver = deps.AgentResolver;
        _agentComm = deps.AgentComm;
        _runService = deps.RunService;
        _transitionService = deps.TransitionService;
        _pendingWorkQuery = deps.PendingWorkQuery;
        _labelService = deps.LabelService;
        _logger = deps.Logger;
        _projectStore = projectStore;
        _consolidationDispatcher = consolidationDispatcher;
        _consolidationRunStore = consolidationRunStore;
    }

    /// <summary>
    /// Wakes the drain loop immediately (e.g., when an agent becomes idle).
    /// </summary>
    public void Signal()
    {
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PendingWorkItemDrainService started, sweep interval: {Interval}s",
            DefaultDrainInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wakeSignal.WaitAsync(DefaultDrainInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await DrainPendingItemsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PendingWorkItemDrainService: unexpected error during drain cycle");
            }
        }
    }

    private async Task DrainPendingItemsAsync(CancellationToken ct)
    {
        await _pendingWorkQuery.GetPendingJobsAsync(ct); // Refresh cached PendingCount for telemetry gauges

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pendingItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending)
            .OrderBy(w => w.TaskType == WorkItemTaskType.Consolidation ? 1 : 0)
            .ThenBy(w => w.CreatedAt)
            .Take(20) // Batch limit per cycle
            .ToListAsync(ct);
        WorkDistributionTelemetry.RecordLastPollEpoch();

        if (pendingItems.Count == 0) { WorkDistributionTelemetry.DispatcherPollCount.Add(1); return; }

        _logger.LogDebug("PendingWorkItemDrainService: {Count} pending item(s) to drain", pendingItems.Count);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested) break;
            if (!await ProcessPendingItemAsync(item, ct)) break;
        }

        WorkDistributionTelemetry.DispatcherPollCount.Add(1); // TODO: placed at end — see comment in original for metric inconsistency risk
    }

    /// <summary>
    /// Processes a single pending work item: resolves an agent, deserializes payload, and dispatches.
    /// Returns <c>false</c> when no idle agents are available at all and the drain loop should stop.
    /// Returns <c>true</c> to continue processing the next item (including the case where this item
    /// was skipped because no agent matched the selector).
    /// </summary>
    private async Task<bool> ProcessPendingItemAsync(WorkItemEntity item, CancellationToken ct)
    {
        if (!TryResolveAgentForItem(item, out var agentId, out var connectionId))
        {
            // Break on "no idle agents at all", continue on "no agent for selector"
            return !string.IsNullOrWhiteSpace(item.AgentSelector);
        }

        if (!TryDeserializePayload(item, agentId, out var request))
            return true;

        // --- Consolidation items: dispatch via IConsolidationDispatchService (token vending at drain time) ---
        if (item.TaskType == WorkItemTaskType.Consolidation)
        {
            await DispatchConsolidationItemAsync(item, request!, agentId, ct);
            return true;
        }

        // --- Pipeline items ---
        // TODO: connectionId is out string? (nullable) from TryResolveAgentForItem but is passed here with
        // null-forgiving operator (!). If resolveResult.ConnectionId is ever null (e.g. agent registered
        // without a connection ID), the null-forgiving silently passes null to DispatchPipelineItemAsync
        // which then forwards it to _agentComm.AssignJobAsync, causing a NullReferenceException at point
        // of use. Add a null-check on connectionId before this call and treat null as a resolution failure.
        // See review finding: Correctness WARNING PendingWorkItemDrainService.cs:150
        if (!await DispatchPipelineItemAsync(item, request!, agentId, connectionId!, ct)) return true;

        await SwapLabelWithRetryAsync(item.Id, request!, ct); // Swap label to agent:in-progress (#997, retries #1579)
        _logger.LogInformation("PendingWorkItemDrainService: assigned WorkItem {WorkItemId} (issue {IssueIdentifier}) to agent {AgentId}",
            item.Id, item.IssueIdentifier, agentId);
        return true;
    }

    /// <summary>
    /// Attempts to resolve an idle agent for the given work item.
    /// Sets <paramref name="agentId"/> and <paramref name="connectionId"/> on success.
    /// Returns false when no agent is available; the caller is responsible for deciding
    /// whether to <c>break</c> (no idle agents at all) or <c>continue</c> (no agent for selector).
    /// </summary>
    private bool TryResolveAgentForItem(
        WorkItemEntity item,
        out AgentId agentId,
        out string? connectionId)
    {
        agentId = default;
        connectionId = null;

        var resolveResult = _agentResolver.ResolveAgent(item.AgentSelector ?? "");
        if (resolveResult is null)
        {
            if (string.IsNullOrWhiteSpace(item.AgentSelector))
                _logger.LogDebug("PendingWorkItemDrainService: no idle agents at all, stopping drain");
            else
                _logger.LogDebug(
                    "PendingWorkItemDrainService: no agent for selector '{Selector}', skipping WorkItem {WorkItemId}",
                    item.AgentSelector, item.Id);
            return false;
        }

        agentId = resolveResult.AgentId;
        connectionId = resolveResult.ConnectionId;
        return true;
    }

    /// <summary>
    /// Attempts to deserialize the work item's JSON payload into a <see cref="JobDistributionRequest"/>.
    /// Releases the reserved agent and logs an error on failure. Returns false when deserialization fails
    /// or the payload is null (caller should <c>continue</c> to the next item).
    /// </summary>
    private bool TryDeserializePayload(
        WorkItemEntity item,
        AgentId agentId,
        out JobDistributionRequest? request)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload ?? "", PipelineJsonOptions.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PendingWorkItemDrainService: failed to deserialize payload for WorkItem {WorkItemId}", item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return false;
        }

        if (request is null)
        {
            _logger.LogError("PendingWorkItemDrainService: null payload for WorkItem {WorkItemId}", item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Dispatches a consolidation work item via <see cref="IConsolidationDispatchService"/>.
    /// Returns <c>true</c> if the item was successfully dispatched to an agent, <c>false</c> in all other cases
    /// (null dispatcher, cancelled run, dispatch failure, or exception). The caller should always <c>continue</c>
    /// to the next item after this call regardless of the return value.
    /// </summary>
    private async Task<bool> DispatchConsolidationItemAsync(
        WorkItemEntity item, JobDistributionRequest request,
        AgentId agentId,
        CancellationToken ct)
    {
        if (_consolidationDispatcher is null || _consolidationRunStore is null)
        {
            _logger.LogError("PendingWorkItemDrainService: consolidation dispatcher not available for WorkItem {WorkItemId}", item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return false;
        }

        // Cancel-during-dispatch race guard: check if run was cancelled while queued
        var runId = request.IssueIdentifier; // RunId stored as IssueIdentifier for consolidation
        var consolidationRun = await _consolidationRunStore.GetByIdAsync(runId, ct);
        if (consolidationRun is null ||
            consolidationRun.Status == ConsolidationRunStatus.Cancelled ||
            consolidationRun.Status == ConsolidationRunStatus.Failed)
        {
            _logger.LogInformation(
                "PendingWorkItemDrainService: consolidation run {RunId} is cancelled/failed, transitioning WorkItem {WorkItemId} to Cancelled",
                runId, item.Id);
            _agentResolver.ReleaseAgent(agentId);
            await _transitionService.TransitionAsync(
                item.Id, WorkItemStatus.Cancelled,
                entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct: ct);
            return false;
        }

        try
        {
            // Transition to Dispatched before dispatch attempt
            await _transitionService.TransitionAsync(
                item.Id, WorkItemStatus.Dispatched,
                entity =>
                {
                    entity.DispatchedAt = DateTimeOffset.UtcNow;
                    entity.AssignedAgentId = agentId.Value;
                }, ct: ct);

            var dispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(
                runId,
                request.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
                string.IsNullOrEmpty(request.ConsolidationTemplateId) ? (TemplateId?)null : (TemplateId)request.ConsolidationTemplateId,
                request.ConsolidationWorkspacePath ?? "",
                agentId.Value,
                ct);

            if (dispatched)
            {
                _agentResolver.AssignJob(agentId, item.Id.ToString());

                WorkDistributionTelemetry.RecordDispatchLatency(DateTimeOffset.UtcNow, item.OriginalEnqueuedAt, item.CreatedAt, item.AgentSelector);

                _logger.LogInformation(
                    "PendingWorkItemDrainService: dispatched consolidation WorkItem {WorkItemId} (run {RunId}) to agent {AgentId}",
                    item.Id, runId, agentId);
                return true;
            }
            else
            {
                // Dispatch failed — revert to Pending for next cycle.
                // Passes ct so the caller's cancellation state is respected during the revert.
                // TryRevertToPendingAsync swallows any exception internally, so a revert failure
                // (e.g., OperationCanceledException during shutdown) will not re-enter the catch
                // block below — the stuck-item detector handles items that could not be reverted.
                // TODO: ReleaseAgent is called before TryRevertToPendingAsync. If the revert is
                // cancelled (ct already cancelled during shutdown), the item is left in Dispatched
                // state with the agent already released — a potential double-release if the catch
                // block is also reached. Confirm that ReleaseAgent is idempotent (safe to call
                // more than once for the same agentId), or restructure to call ReleaseAgent only
                // after the transition completes successfully.
                _agentResolver.ReleaseAgent(agentId);
                await TryRevertToPendingAsync(item.Id, incrementRetryCount: false, ct: ct);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PendingWorkItemDrainService: consolidation dispatch failed for WorkItem {WorkItemId}",
                item.Id);
            _agentResolver.ReleaseAgent(agentId);
            // Revert WorkItem from Dispatched to Pending so it's available for the next drain cycle.
            // Uses CancellationToken.None so that graceful shutdown does not prevent the revert.
            await TryRevertToPendingAsync(item.Id, incrementRetryCount: false);
            return false;
        }
    }

    private async Task<bool> DispatchPipelineItemAsync(
        WorkItemEntity item, JobDistributionRequest request,
        AgentId agentId, string connectionId,
        CancellationToken ct)
    {
        var dispatchedSuccessfully = false;
        try
        {
            // DB transition first: in-memory state only reflects confirmed DB state.
            // If TransitionAsync fails, no in-memory cleanup is needed.
            // This also ensures the agent's JobAccepted → Running transition is valid
            // (Dispatched → Running, not Pending → Running which is rejected).
            var dispatchTime = DateTimeOffset.UtcNow;
            await _transitionService.TransitionAsync(
                item.Id,
                WorkItemStatus.Dispatched,
                entity =>
                {
                    entity.DispatchedAt = dispatchTime;
                    entity.AssignedAgentId = agentId.Value;
                },
                ct: ct);

            dispatchedSuccessfully = true;

            EnsureInMemoryRunRegistered(request, agentId.Value, dispatchTime, item);

            var message = DbWorkDistributorBase.BuildJobAssignmentMessage(item.Id, request);

            // Inject project secrets at delivery time (not serialized in WorkItem payload for security)
            if (_projectStore is not null && !string.IsNullOrEmpty(request.ProjectId))
            {
                var project = await _projectStore.GetProjectByIdAsync(request.ProjectId, ct);
                if (project?.Secrets is { Count: > 0 })
                    message = message with { ProjectSecrets = project.Secrets };
            }

            await _agentComm.AssignJobAsync(connectionId, message, ct);

            _agentResolver.AssignJob(agentId, item.Id.ToString());

            WorkDistributionTelemetry.RecordDispatchLatency(DateTimeOffset.UtcNow, item.OriginalEnqueuedAt, item.CreatedAt, item.AgentSelector);

            return true;
        }
        catch (Exception ex)
        {
            await HandlePipelineDispatchFailureAsync(item, request, agentId, dispatchedSuccessfully, ex);
            return false;
        }
    }

    /// <summary>
    /// Updates or re-creates the in-memory <see cref="PipelineRun"/> after the DB transition to
    /// Dispatched has succeeded. Handles the orchestrator-restart recovery case where the run was
    /// lost from memory and must be re-created from the distribution request.
    /// </summary>
    private void EnsureInMemoryRunRegistered(
        JobDistributionRequest request,
        string agentId,
        DateTimeOffset dispatchTime,
        WorkItemEntity item)
    {
        if (string.IsNullOrEmpty(request.RunId))
            return;

        var run = _runService.GetRun(request.RunId);
        if (run is not null)
        {
            run.AgentId = agentId;
        }
        else
        {
            // Orchestrator restarted — in-memory PipelineRun was lost.
            // Re-create it from the serialized request payload.
            var recreatedRun = PipelineRunFactory.FromDistributionRequest(
                request, agentId, startedAt: item.DispatchedAt ?? item.CreatedAt);
            _runService.AddRun(recreatedRun);
            _logger.LogInformation(
                "PendingWorkItemDrainService: re-created in-memory PipelineRun {RunId} for issue {IssueIdentifier} (orchestrator restart recovery)",
                request.RunId, request.IssueIdentifier);
        }

        // Update in-memory PipelineRun StartedAt to actual dispatch time (BUG-14).
        // Without this, StartedAt reflects preparation/enqueue time which can be
        // hours earlier for queued work, inflating the Duration shown in the UI.
        _runService.GetRun(request.RunId)?.ResetStartedAt(dispatchTime);
    }

    /// <summary>
    /// Handles a pipeline dispatch failure: releases the reserved agent, optionally removes the
    /// in-memory run (if the DB transition had succeeded), reverts to Pending, and performs a
    /// direct RetryCount increment if the DB transition itself failed.
    /// <paramref name="dispatchedSuccessfully"/> must be the value captured inside the <c>try</c>
    /// block — passed explicitly to avoid stale-closure risk.
    /// </summary>
    private async Task HandlePipelineDispatchFailureAsync(
        WorkItemEntity item,
        JobDistributionRequest request,
        AgentId agentId,
        bool dispatchedSuccessfully,
        Exception ex)
    {
        _logger.LogError(ex,
            "PendingWorkItemDrainService: dispatch failed for WorkItem {WorkItemId}",
            item.Id);
        _agentResolver.ReleaseAgent(agentId);

        // Clean up in-memory PipelineRun only if it was actually registered
        // (DB transition succeeded, so in-memory registration happened).
        // If TransitionAsync failed, the run was never added — no cleanup needed.
        if (dispatchedSuccessfully && !string.IsNullOrEmpty(request.RunId))
            _runService.RemoveRun(request.RunId);

        // Revert to Pending for retry on next drain cycle.
        // Safe regardless of where the exception occurred:
        // - If TransitionAsync(Dispatched) itself failed, item is still Pending → TransitionAsync
        //   returns true idempotently (already at target).
        // - If exception was after Dispatched transition, item reverts Dispatched → Pending (valid).
        await TryRevertToPendingAsync(item.Id, incrementRetryCount: true);

        // If TransitionAsync(Dispatched) itself failed, the item was already Pending and the
        // idempotent path above skipped the mutate callback — RetryCount was NOT incremented.
        // Perform a direct DB update to prevent infinite retry loops.
        if (!dispatchedSuccessfully)
        {
            try
            {
                await using var retryDb = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
                var entity = await retryDb.WorkItems.FindAsync([item.Id], CancellationToken.None);
                if (entity is not null)
                {
                    entity.RetryCount++;
                    await retryDb.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx,
                    "PendingWorkItemDrainService: failed to increment RetryCount for WorkItem {WorkItemId} after dispatch-transition failure",
                    item.Id);
            }
        }
    }

    /// <summary>
    /// Attempts to revert a work item from Dispatched back to Pending after a dispatch failure.
    /// Swallows any exception from the transition and logs a warning — the stuck-item
    /// detector will handle items that could not be reverted.
    /// </summary>
    /// <param name="workItemId">The work item to revert.</param>
    /// <param name="incrementRetryCount">
    /// <c>true</c> for pipeline dispatch failures (RetryCount must increment to prevent infinite loops);
    /// <c>false</c> for consolidation dispatch failures (RetryCount is not incremented).
    /// </param>
    /// <param name="ct">
    /// Token to use for the transition. Defaults to <see cref="CancellationToken.None"/> so that
    /// catch-block callers are not affected by an already-cancelled token during graceful shutdown.
    /// Pass the caller's token when the revert should respect the caller's cancellation state
    /// (e.g., the consolidation false-return path).
    /// </param>
    private async Task TryRevertToPendingAsync(Guid workItemId, bool incrementRetryCount,
        CancellationToken ct = default)
    {
        try
        {
            await _transitionService.TransitionAsync(
                workItemId, WorkItemStatus.Pending,
                entity =>
                {
                    entity.DispatchedAt = null;
                    entity.AssignedAgentId = null;
                    if (incrementRetryCount) entity.RetryCount++;
                }, ct: ct);
        }
        catch (Exception revertEx)
        {
            _logger.LogWarning(revertEx,
                "PendingWorkItemDrainService: failed to revert WorkItem {WorkItemId} to Pending after dispatch failure — stuck-item detector will handle",
                workItemId);
        }
    }

    /// <summary>
    /// Swaps the work item label to agent:in-progress with exponential backoff retry.
    /// Flags for reconciliation if all attempts fail or if shutdown occurs mid-retry.
    /// </summary>
    private async Task SwapLabelWithRetryAsync(Guid workItemId, JobDistributionRequest request, CancellationToken ct)
    {
        const int maxLabelSwapAttempts = 3; // 1 initial + 2 retries
        var providerForLabel = request.RunType == PipelineRunType.Review
            ? request.RepoProviderConfigId
            : request.IssueProviderConfigId;
        var targetKind = request.RunType == PipelineRunType.Review
            ? LabelTargetKind.PullRequest
            : LabelTargetKind.Issue;

        bool labelSwapCompleted = false;
        try
        {
            for (int attempt = 1; attempt <= maxLabelSwapAttempts; attempt++)
            {
                if (await TrySwapLabelOnceAsync(workItemId, request, providerForLabel, targetKind, attempt, maxLabelSwapAttempts, ct))
                {
                    labelSwapCompleted = true;
                    break;
                }
            }
        }
        finally
        {
            // If shutdown occurred during backoff (Task.Delay throws OCE) or during
            // SwapLabelStrictAsync itself, the label swap never completed. Flag for
            // reconciliation so OrphanedLabelRecoveryService can fix the stale label. (#1681)
            if (!labelSwapCompleted && ct.IsCancellationRequested)
            {
                await FlagForLabelReconciliationAsync(workItemId);
            }
        }
    }

    /// <summary>
    /// Performs a single label swap attempt. Returns <c>true</c> if the swap succeeded
    /// (caller should set <c>labelSwapCompleted = true</c> and break). Returns <c>false</c>
    /// if the attempt failed with a non-cancellation exception (caller should proceed to the
    /// next attempt or stop if retries are exhausted). Propagates <see cref="OperationCanceledException"/>
    /// so the outer <c>finally</c> block can flag for reconciliation on shutdown.
    /// </summary>
    private async Task<bool> TrySwapLabelOnceAsync(
        Guid workItemId,
        JobDistributionRequest request,
        ProviderConfigId providerForLabel,
        LabelTargetKind targetKind,
        int attempt,
        int maxAttempts,
        CancellationToken ct)
    {
        try
        {
            await _labelService.SwapLabelStrictAsync(
                providerForLabel, request.IssueIdentifier, AgentLabels.InProgress, targetKind, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)); // 200ms, 400ms
                _logger.LogWarning(ex,
                    "PendingWorkItemDrainService: label swap attempt {Attempt}/{Max} failed, retrying in {Delay}ms",
                    attempt, maxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "PendingWorkItemDrainService: label swap exhausted all {Max} attempts for WorkItem {WorkItemId} — flagging for reconciliation",
                    maxAttempts, workItemId);
                await FlagForLabelReconciliationAsync(workItemId);
            }
            return false;
        }
    }

    /// <summary>
    /// Flags a work item for label reconciliation after the retry loop for SwapLabelStrictAsync
    /// has been exhausted. Uses a separate DbContext to avoid interfering with the outer query.
    /// </summary>
    private async Task FlagForLabelReconciliationAsync(Guid workItemId)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            var entity = await db.WorkItems.FindAsync([workItemId], CancellationToken.None);
            if (entity is not null)
            {
                entity.NeedsLabelReconciliation = true;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PendingWorkItemDrainService: failed to flag WorkItem {WorkItemId} for label reconciliation",
                workItemId);
        }
    }
}
