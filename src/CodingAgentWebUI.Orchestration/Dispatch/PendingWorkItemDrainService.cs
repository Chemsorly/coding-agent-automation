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
            var shouldBreak = await ProcessPendingItemAsync(item, ct);
            if (shouldBreak) break;
        }

        // TODO: DispatcherPollCount is placed at end-of-method rather than start-of-call. If an unhandled exception
        // escapes the foreach loop after RecordLastPollEpoch(), the staleness gauge updates but the poll counter
        // does not increment, creating metric inconsistency. Matches K8s DispatchService pattern but deviates from
        // the stated requirement text. Low risk due to inner try-catch coverage.
        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }

    /// <summary>
    /// Processes a single pending work item in the drain loop.
    /// Returns true if the outer loop should break (no more agents available).
    /// </summary>
    private async Task<bool> ProcessPendingItemAsync(WorkItemEntity item, CancellationToken ct)
    {
        var resolveResult = _agentResolver.ResolveAgent(item.AgentSelector ?? "");
        if (resolveResult is null)
        {
            if (string.IsNullOrWhiteSpace(item.AgentSelector))
            {
                _logger.LogDebug("PendingWorkItemDrainService: no idle agents at all, stopping drain");
                return true; // break
            }
            _logger.LogDebug(
                "PendingWorkItemDrainService: no agent for selector '{Selector}', skipping WorkItem {WorkItemId}",
                item.AgentSelector, item.Id);
            return false; // continue
        }

        var (request, skip) = ResolveAndValidateRequest(item);
        if (skip)
        {
            _agentResolver.ReleaseAgent(resolveResult.AgentId);
            return false; // continue
        }

        var connectionId = resolveResult.ConnectionId;
        var agentId = resolveResult.AgentId;

        // --- Consolidation items ---
        if (item.TaskType == WorkItemTaskType.Consolidation)
        {
            await HandleConsolidationItemAsync(item, request!, agentId, ct);
            // TODO: HandleConsolidationItemAsync does not return a result, so SwapLabelWithRetryAsync
            // and the success log below are never reached for consolidation items. This matches the
            // original behavior (original consolidation block also ended with `continue`), so there
            // is no behavioral regression — but it means label-swap is consolidation-only skipped.
            // (review-findings: Correctness WARNING)
            return false; // continue
        }

        // --- Pipeline items ---
        await DispatchPipelineItemAsync(item, request!, agentId, connectionId, ct);
        return false;
    }

    /// <summary>
    /// Dispatches a pipeline (non-consolidation) work item, handles retry counting,
    /// label swap, and success logging.
    /// </summary>
    private async Task DispatchPipelineItemAsync(
        WorkItemEntity item,
        JobDistributionRequest request,
        string agentId,
        string connectionId,
        CancellationToken ct)
    {
        var result = await HandlePipelineItemAsync(item, request, agentId, connectionId, ct);

        // If TransitionAsync(Dispatched) itself failed, perform direct RetryCount increment
        if (!result.FullyCompleted && !result.DispatchedSuccessfully)
        {
            await IncrementRetryCountDirectAsync(item.Id);
        }

        if (!result.FullyCompleted)
            return;

        // Swap label to agent:in-progress now that an agent is actually working on it (#997)
        await SwapLabelWithRetryAsync(item.Id, request, ct);

        _logger.LogInformation(
            "PendingWorkItemDrainService: assigned WorkItem {WorkItemId} (issue {IssueIdentifier}) to agent {AgentId}",
            item.Id, item.IssueIdentifier, agentId);
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
                try
                {
                    await _labelService.SwapLabelStrictAsync(
                        providerForLabel, request.IssueIdentifier, AgentLabels.InProgress, targetKind, ct);
                    labelSwapCompleted = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt < maxLabelSwapAttempts)
                    {
                        var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)); // 200ms, 400ms
                        _logger.LogWarning(ex,
                            "PendingWorkItemDrainService: label swap attempt {Attempt}/{Max} failed, retrying in {Delay}ms",
                            attempt, maxLabelSwapAttempts, delay.TotalMilliseconds);
                        await Task.Delay(delay, ct);
                    }
                    else
                    {
                        _logger.LogWarning(ex,
                            "PendingWorkItemDrainService: label swap exhausted all {Max} attempts for WorkItem {WorkItemId} — flagging for reconciliation",
                            maxLabelSwapAttempts, workItemId);
                        await FlagForLabelReconciliationAsync(workItemId);
                    }
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

    /// <summary>
    /// Result of a pipeline dispatch attempt. Carries whether the DB transition to Dispatched
    /// succeeded, so the caller can drive the two-path RetryCount increment correctly.
    /// </summary>
    private readonly record struct PipelineDispatchResult(bool DispatchedSuccessfully, bool FullyCompleted);

    // TODO: ResolveAndValidateRequest has two distinct error branches (JsonException on deserialization,
    // null result after successful parse) that are not covered by unit tests targeting this drain service
    // directly. The E2E test K8sMode_AgentFetchesAssignment_NullPayload_Returns404 covers null payload
    // at the API layer only. Add a unit test: insert a WorkItem with Payload="not json", trigger the
    // drain, and verify _agentResolver.ReleaseAgent is called for the pipeline path.
    // (review-findings: TestQualityReviewer WARNING; Correctness WARNING re: AsNoTracking + null Payload)
    private (JobDistributionRequest? request, bool skip) ResolveAndValidateRequest(
        WorkItemEntity item)
    {
        JobDistributionRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JobDistributionRequest>(
                item.Payload ?? "", PipelineJsonOptions.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PendingWorkItemDrainService: failed to deserialize payload for WorkItem {WorkItemId}", item.Id);
            return (null, true);
        }

        if (request is null)
        {
            _logger.LogError(
                "PendingWorkItemDrainService: null payload for WorkItem {WorkItemId}", item.Id);
            return (null, true);
        }

        return (request, false);
    }

    private async Task HandleConsolidationItemAsync(
        WorkItemEntity item, JobDistributionRequest request, string agentId, CancellationToken ct)
    {
        if (_consolidationDispatcher is null || _consolidationRunStore is null)
        {
            _logger.LogError(
                "PendingWorkItemDrainService: consolidation dispatcher not available for WorkItem {WorkItemId}",
                item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return;
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
            return;
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
                }, ct: ct);

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

                var latency = (DateTimeOffset.UtcNow - (item.OriginalEnqueuedAt ?? item.CreatedAt)).TotalSeconds;
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
                    }, ct: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PendingWorkItemDrainService: consolidation dispatch failed for WorkItem {WorkItemId}",
                item.Id);
            _agentResolver.ReleaseAgent(agentId);
            await RevertConsolidationToPendingAsync(item.Id);
        }
    }

    private async Task RevertConsolidationToPendingAsync(Guid workItemId)
    {
        try
        {
            await _transitionService.TransitionAsync(
                workItemId, WorkItemStatus.Pending,
                entity =>
                {
                    entity.DispatchedAt = null;
                    entity.AssignedAgentId = null;
                }, ct: CancellationToken.None);
        }
        catch (Exception revertEx)
        {
            _logger.LogWarning(revertEx,
                "PendingWorkItemDrainService: failed to revert WorkItem {WorkItemId} to Pending after dispatch exception — stuck-item detector will handle",
                workItemId);
        }
    }

    private async Task<PipelineDispatchResult> HandlePipelineItemAsync(
        WorkItemEntity item, JobDistributionRequest request, string agentId, string connectionId, CancellationToken ct)
    {
        var dispatchedSuccessfully = false;
        try
        {
            var dispatchTime = DateTimeOffset.UtcNow;
            await _transitionService.TransitionAsync(
                item.Id,
                WorkItemStatus.Dispatched,
                entity =>
                {
                    entity.DispatchedAt = dispatchTime;
                    entity.AssignedAgentId = agentId;
                },
                ct: ct);

            dispatchedSuccessfully = true;

            // Update in-memory PipelineRun with agent ID.
            if (!string.IsNullOrEmpty(request.RunId))
            {
                var run = _runService.GetRun(request.RunId);
                if (run is not null)
                {
                    run.AgentId = agentId;
                }
                else
                {
                    var recreatedRun = PipelineRunFactory.FromDistributionRequest(
                        request, agentId, startedAt: item.DispatchedAt ?? item.CreatedAt);
                    _runService.AddRun(recreatedRun);
                    _logger.LogInformation(
                        "PendingWorkItemDrainService: re-created in-memory PipelineRun {RunId} for issue {IssueIdentifier} (orchestrator restart recovery)",
                        request.RunId, request.IssueIdentifier);
                }
            }

            // Update in-memory PipelineRun StartedAt to actual dispatch time (BUG-14).
            if (!string.IsNullOrEmpty(request.RunId))
            {
                _runService.GetRun(request.RunId)?.ResetStartedAt(dispatchTime);
            }

            var message = DbWorkDistributorBase.BuildJobAssignmentMessage(item.Id, request);

            // Inject project secrets at delivery time
            if (_projectStore is not null && !string.IsNullOrEmpty(request.ProjectId))
            {
                var project = await _projectStore.GetProjectByIdAsync(request.ProjectId, ct);
                if (project?.Secrets is { Count: > 0 })
                    message = message with { ProjectSecrets = project.Secrets };
            }

            await _agentComm.AssignJobAsync(connectionId, message, ct);

            _agentResolver.AssignJob(agentId, item.Id.ToString());

            var latency = (DateTimeOffset.UtcNow - (item.OriginalEnqueuedAt ?? item.CreatedAt)).TotalSeconds;
            WorkDistributionTelemetry.DispatchLatency.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));
            WorkDistributionTelemetry.PendingDuration.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));

            return new PipelineDispatchResult(dispatchedSuccessfully, FullyCompleted: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PendingWorkItemDrainService: dispatch failed for WorkItem {WorkItemId}",
                item.Id);
            _agentResolver.ReleaseAgent(agentId);

            if (dispatchedSuccessfully && !string.IsNullOrEmpty(request.RunId))
            {
                _runService.RemoveRun(request.RunId);
            }

            await RevertPipelineToPendingAsync(item.Id);

            return new PipelineDispatchResult(dispatchedSuccessfully, FullyCompleted: false);
        }
    }

    private async Task RevertPipelineToPendingAsync(Guid workItemId)
    {
        try
        {
            await _transitionService.TransitionAsync(
                workItemId, WorkItemStatus.Pending,
                entity =>
                {
                    entity.DispatchedAt = null;
                    entity.AssignedAgentId = null;
                    entity.RetryCount++;
                }, ct: CancellationToken.None);
        }
        catch (Exception revertEx)
        {
            _logger.LogWarning(revertEx,
                "PendingWorkItemDrainService: failed to revert WorkItem {WorkItemId} to Pending after dispatch failure — stuck-item detector will handle",
                workItemId);
        }
    }

    private async Task IncrementRetryCountDirectAsync(Guid workItemId)
    {
        try
        {
            await using var retryDb = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            var entity = await retryDb.WorkItems.FindAsync([workItemId], CancellationToken.None);
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
                workItemId);
        }
    }
}
