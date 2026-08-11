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
    private readonly WorkItemTransitionService _transitionService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly LabelSwapService _labelSwapService;
    private readonly DispatchRevertService _dispatchRevertHandler;
    private readonly DispatchAttemptService _dispatchAttemptService;
    private readonly IProjectStore? _projectStore;
    private readonly IConsolidationDispatchService? _consolidationDispatcher;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly ILogger<PendingWorkItemDrainService> _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(5);

    public PendingWorkItemDrainService(
        DrainServiceDependencies deps,
        LabelSwapService labelSwapService,
        DispatchRevertService dispatchRevertHandler,
        DispatchAttemptService dispatchAttemptService,
        IProjectStore? projectStore = null,
        IConsolidationDispatchService? consolidationDispatcher = null,
        IConsolidationRunStore? consolidationRunStore = null)
    {
        _dbFactory = deps.DbFactory;
        _agentResolver = deps.AgentResolver;
        _agentComm = deps.AgentComm;
        _transitionService = deps.TransitionService;
        _pendingWorkQuery = deps.PendingWorkQuery;
        _logger = deps.Logger;
        _labelSwapService = labelSwapService;
        _dispatchRevertHandler = dispatchRevertHandler;
        _dispatchAttemptService = dispatchAttemptService;
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

        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
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
        if (!await DispatchPipelineItemAsync(item, request!, agentId, connectionId!, ct)) return true;

        await _labelSwapService.SwapLabelWithRetryAsync(item.Id, request!, ct); // Swap label to agent:in-progress (#997, retries #1579)
        if (_logger.IsEnabled(LogLevel.Information))
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
        var runId = request.IssueIdentifier.Value; // RunId stored as IssueIdentifier for consolidation
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
            // Transition to Dispatched before dispatch attempt.
            // Delegates to DispatchAttemptService to keep transition-to-Dispatched logic in one place (#1914).
            await _dispatchAttemptService.TransitionToDispatchedAsync(item.Id, agentId, ct);

            var dispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(
                runId,
                request.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
                string.IsNullOrEmpty(request.ConsolidationTemplateId) ? (TemplateId?)null : (TemplateId)request.ConsolidationTemplateId,
                request.ConsolidationWorkspacePath ?? "",
                agentId,
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
                // NOTE: ReleaseAgent is called before TryRevertToPendingAsync. If the revert is
                // cancelled (ct already cancelled during shutdown), the item is left in Dispatched
                // state with the agent already released — the stuck-item detector handles recovery.
                // ReleaseAgent is idempotent (safe to call more than once for the same agentId),
                // so a double-release via the catch block below is not a correctness risk.
                _agentResolver.ReleaseAgent(agentId);
                // Revert directly via _dispatchRevertHandler (functionally equivalent to RevertOnFailureAsync).
                await _dispatchRevertHandler.TryRevertToPendingAsync(item.Id, incrementRetryCount: false, ct: ct);
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
            // Uses CancellationToken.None explicitly: graceful shutdown must not prevent the revert.
            await _dispatchRevertHandler.TryRevertToPendingAsync(item.Id, incrementRetryCount: false, ct: CancellationToken.None);
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
            // Delegates to DispatchAttemptService to keep transition-to-Dispatched logic in one place (#1914).
            // Note: dispatchTime is captured before the call; DispatchAttemptService sets entity.DispatchedAt
            // internally, so the two timestamps may differ by a sub-millisecond skew in practice.
            var dispatchTime = DateTimeOffset.UtcNow;
            await _dispatchAttemptService.TransitionToDispatchedAsync(item.Id, agentId, ct);

            dispatchedSuccessfully = true;

            _dispatchRevertHandler.EnsureInMemoryRunRegistered(request, agentId.Value, dispatchTime, item);

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
            await _dispatchRevertHandler.HandlePipelineDispatchFailureAsync(item, request, agentId, dispatchedSuccessfully, ex);
            return false;
        }
    }
}
