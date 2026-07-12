using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
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
    private readonly ILabelSwapper _labelSwapper;
    private readonly IProjectStore? _projectStore;
    private readonly IConsolidationDispatcher? _consolidationDispatcher;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly ILogger<PendingWorkItemDrainService> _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(5);

    public PendingWorkItemDrainService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ISignalRWorkDistributorAgentResolver agentResolver,
        IAgentCommunication agentComm,
        IOrchestratorRunService runService,
        WorkItemTransitionService transitionService,
        IPendingWorkQuery pendingWorkQuery,
        ILabelSwapper labelSwapper,
        ILogger<PendingWorkItemDrainService> logger,
        IProjectStore? projectStore = null,
        IConsolidationDispatcher? consolidationDispatcher = null,
        IConsolidationRunStore? consolidationRunStore = null)
    {
        _dbFactory = dbFactory;
        _agentResolver = agentResolver;
        _agentComm = agentComm;
        _runService = runService;
        _transitionService = transitionService;
        _pendingWorkQuery = pendingWorkQuery;
        _labelSwapper = labelSwapper;
        _logger = logger;
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
        // Refresh the cached PendingCount for telemetry gauges (keeps metric fresh even without UI)
        await _pendingWorkQuery.GetPendingJobsAsync(ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pendingItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending)
            .OrderBy(w => w.TaskType == WorkItemTaskType.Consolidation ? 1 : 0)
            .ThenBy(w => w.CreatedAt)
            .Take(20) // Batch limit per cycle
            .ToListAsync(ct);

        WorkDistributionTelemetry.RecordLastPollEpoch();

        if (pendingItems.Count == 0)
        {
            WorkDistributionTelemetry.DispatcherPollCount.Add(1);
            return;
        }

        _logger.LogDebug("PendingWorkItemDrainService: {Count} pending item(s) to drain", pendingItems.Count);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested) break;

            var resolveResult = _agentResolver.ResolveAgent(item.AgentSelector ?? "");
            if (resolveResult is null)
            {
                // No idle agent for this selector. If the selector is empty (matches any agent),
                // then no agents are idle at all — stop draining entirely.
                if (string.IsNullOrWhiteSpace(item.AgentSelector))
                {
                    _logger.LogDebug("PendingWorkItemDrainService: no idle agents at all, stopping drain");
                    break;
                }
                // Label-constrained item with no matching agent — skip, try the next item
                _logger.LogDebug(
                    "PendingWorkItemDrainService: no agent for selector '{Selector}', skipping WorkItem {WorkItemId}",
                    item.AgentSelector, item.Id);
                continue;
            }

            // Deserialize the original request from payload
            JobDistributionRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload ?? "", PipelineJsonOptions.Default);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PendingWorkItemDrainService: failed to deserialize payload for WorkItem {WorkItemId}", item.Id);
                _agentResolver.ReleaseAgent(resolveResult.AgentId);
                continue;
            }

            if (request is null)
            {
                _logger.LogError("PendingWorkItemDrainService: null payload for WorkItem {WorkItemId}", item.Id);
                _agentResolver.ReleaseAgent(resolveResult.AgentId);
                continue;
            }

            var connectionId = resolveResult.ConnectionId;
            var agentId = resolveResult.AgentId;

            // --- Consolidation items: dispatch via IConsolidationDispatcher (token vending at drain time) ---
            if (item.TaskType == WorkItemTaskType.Consolidation)
            {
                if (_consolidationDispatcher is null || _consolidationRunStore is null)
                {
                    _logger.LogError("PendingWorkItemDrainService: consolidation dispatcher not available for WorkItem {WorkItemId}", item.Id);
                    _agentResolver.ReleaseAgent(agentId);
                    continue;
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
                        entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct);
                    continue;
                }

                try
                {
                    // Transition to Dispatched before dispatch attempt
                    await _transitionService.TransitionAsync(
                        item.Id, WorkItemStatus.Dispatched,
                        entity =>
                        {
                            entity.DispatchedAt = DateTimeOffset.UtcNow;
                            entity.AssignedAgentId = agentId;
                        }, ct);

                    var dispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(
                        runId,
                        request.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
                        request.ConsolidationTemplateId,
                        request.ConsolidationWorkspacePath ?? "",
                        agentId,
                        ct);

                    if (dispatched)
                    {
                        _agentResolver.AssignJob(agentId, item.Id.ToString());

                        var latency = (DateTimeOffset.UtcNow - item.CreatedAt).TotalSeconds;
                        // TODO: Use OriginalEnqueuedAt ?? CreatedAt instead of CreatedAt to reflect true time since original enqueue for re-dispatched items (see BUG-10 review findings)
                        WorkDistributionTelemetry.DispatchLatency.Record(latency,
                            new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));
                        WorkDistributionTelemetry.PendingDuration.Record(latency,
                            new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));

                        _logger.LogInformation(
                            "PendingWorkItemDrainService: dispatched consolidation WorkItem {WorkItemId} (run {RunId}) to agent {AgentId}",
                            item.Id, runId, agentId);
                    }
                    else
                    {
                        // Dispatch failed — revert to Pending for next cycle
                        _agentResolver.ReleaseAgent(agentId);
                        await _transitionService.TransitionAsync(
                            item.Id, WorkItemStatus.Pending,
                            entity =>
                            {
                                entity.DispatchedAt = null;
                                entity.AssignedAgentId = null;
                            }, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "PendingWorkItemDrainService: consolidation dispatch failed for WorkItem {WorkItemId}",
                        item.Id);
                    _agentResolver.ReleaseAgent(agentId);
                    // Revert WorkItem from Dispatched to Pending so it's available for the next drain cycle
                    // (previously this was left stuck in Dispatched until the stuck-item detector fired ~5 min later)
                    try
                    {
                        await _transitionService.TransitionAsync(
                            item.Id, WorkItemStatus.Pending,
                            entity =>
                            {
                                entity.DispatchedAt = null;
                                entity.AssignedAgentId = null;
                            }, ct);
                    }
                    catch (Exception revertEx)
                    {
                        _logger.LogWarning(revertEx,
                            "PendingWorkItemDrainService: failed to revert WorkItem {WorkItemId} to Pending after dispatch exception — stuck-item detector will handle",
                            item.Id);
                    }
                }

                continue;
            }

            // --- Pipeline items: existing path ---
            try
            {
                // Update in-memory PipelineRun with agent ID.
                // If the run is not in memory (orchestrator restart scenario), re-create it
                // so that HeartbeatMonitor and run-tracking continue to function correctly.
                if (!string.IsNullOrEmpty(request.RunId))
                {
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
                }

                // Transition to Dispatched in DB BEFORE sending via SignalR.
                // This ensures the agent's JobAccepted → Running transition is valid
                // (Dispatched → Running, not Pending → Running which is rejected).
                await _transitionService.TransitionAsync(
                    item.Id,
                    WorkItemStatus.Dispatched,
                    entity =>
                    {
                        entity.DispatchedAt = DateTimeOffset.UtcNow;
                        entity.AssignedAgentId = agentId;
                    },
                    ct);

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

                var latency = (DateTimeOffset.UtcNow - item.CreatedAt).TotalSeconds;
                WorkDistributionTelemetry.DispatchLatency.Record(latency,
                    new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));
                WorkDistributionTelemetry.PendingDuration.Record(latency,
                    new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PendingWorkItemDrainService: dispatch failed for WorkItem {WorkItemId}",
                    item.Id);
                _agentResolver.ReleaseAgent(agentId);

                // Clean up in-memory PipelineRun (idempotent — TryRemove is a no-op if key doesn't exist,
                // which handles the case where the exception fired before run creation/modification)
                if (!string.IsNullOrEmpty(request.RunId))
                {
                    _runService.RemoveRun(request.RunId);
                }

                // Revert to Pending for retry on next drain cycle.
                // Safe regardless of where the exception occurred:
                // - If TransitionAsync(Dispatched) itself failed, item is still Pending → TransitionAsync
                //   returns true idempotently (already at target).
                // - If exception was after Dispatched transition, item reverts Dispatched → Pending (valid).
                // TODO: The idempotent path (item already Pending) does NOT invoke the mutate callback,
                // so RetryCount won't increment if the exception occurred during TransitionAsync(Dispatched).
                // Consider checking the return value and manually incrementing RetryCount in that case.
                try
                {
                    // TODO: Using the same stoppingToken (ct) here means the revert will also fail if
                    // the original exception was due to cancellation (shutdown). Consider using
                    // CancellationToken.None to ensure revert completes during graceful shutdown.
                    await _transitionService.TransitionAsync(
                        item.Id, WorkItemStatus.Pending,
                        entity =>
                        {
                            entity.DispatchedAt = null;
                            entity.AssignedAgentId = null;
                            entity.RetryCount++;
                        }, ct);
                }
                catch (Exception revertEx)
                {
                    _logger.LogWarning(revertEx,
                        "PendingWorkItemDrainService: failed to revert WorkItem {WorkItemId} to Pending after dispatch failure — stuck-item detector will handle",
                        item.Id);
                }

                continue;
            }

            // Swap label to agent:in-progress now that an agent is actually working on it (#997)
            // TODO: If SwapLabelAsync fails, the label stays as agent:next even though the agent is working.
            //       Consider a retry or reconciliation mechanism to avoid stale labels in the opposite direction.
            try
            {
                var providerForLabel = request.RunType == PipelineRunType.Review
                    ? request.RepoProviderConfigId
                    : request.IssueProviderConfigId;
                var targetKind = request.RunType == PipelineRunType.Review
                    ? LabelTargetKind.PullRequest
                    : LabelTargetKind.Issue;

                await _labelSwapper.SwapLabelAsync(
                    providerForLabel, request.IssueIdentifier, AgentLabels.InProgress, targetKind, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "PendingWorkItemDrainService: failed to swap label to in-progress for WorkItem {WorkItemId} (non-fatal)",
                    item.Id);
            }

            _logger.LogInformation(
                "PendingWorkItemDrainService: assigned WorkItem {WorkItemId} (issue {IssueIdentifier}) to agent {AgentId}",
                item.Id, item.IssueIdentifier, agentId);
        }

        // TODO: DispatcherPollCount is placed at end-of-method rather than start-of-call. If an unhandled exception
        // escapes the foreach loop after RecordLastPollEpoch(), the staleness gauge updates but the poll counter
        // does not increment, creating metric inconsistency. Matches K8s DispatchService pattern but deviates from
        // the stated requirement text. Low risk due to inner try-catch coverage.
        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }
}
