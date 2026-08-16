using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Dispatches a consolidation work item from the drain loop to an agent.
/// Extracted from <see cref="PendingWorkItemDrainService.DispatchConsolidationItemAsync"/>
/// (issue #2063) to reduce <see cref="PendingWorkItemDrainService"/> to a thin coordinator.
/// </summary>
/// <remarks>
/// Handles three dispatch outcomes:
/// <list type="bullet">
///   <item><description><b>Cancelled/failed run guard</b> — if the consolidation run is no longer
///   active, transitions the WorkItem to <see cref="WorkItemStatus.Cancelled"/> and returns
///   <c>false</c>.</description></item>
///   <item><description><b>Successful dispatch</b> — transitions to Dispatched, calls
///   <see cref="IConsolidationDispatchService.TryDispatchToAgentAsync"/>, records telemetry,
///   returns <c>true</c>.</description></item>
///   <item><description><b>Failed/exceptional dispatch</b> — releases the agent, reverts to
///   Pending, returns <c>false</c>.</description></item>
/// </list>
/// </remarks>
public sealed class ConsolidationDrainDispatcher : IConsolidationDrainDispatcher
{
    private readonly IConsolidationDispatchService _consolidationDispatcher;
    private readonly IConsolidationRunStore _consolidationRunStore;
    private readonly DispatchAttemptService _dispatchAttemptService;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ISignalRWorkDistributorAgentResolver _agentResolver;
    private readonly ILogger<ConsolidationDrainDispatcher> _logger;

    public ConsolidationDrainDispatcher(
        IConsolidationDispatchService consolidationDispatcher,
        IConsolidationRunStore consolidationRunStore,
        DispatchAttemptService dispatchAttemptService,
        WorkItemTransitionService transitionService,
        ISignalRWorkDistributorAgentResolver agentResolver,
        ILogger<ConsolidationDrainDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(consolidationDispatcher);
        ArgumentNullException.ThrowIfNull(consolidationRunStore);
        ArgumentNullException.ThrowIfNull(dispatchAttemptService);
        ArgumentNullException.ThrowIfNull(transitionService);
        ArgumentNullException.ThrowIfNull(agentResolver);
        ArgumentNullException.ThrowIfNull(logger);
        _consolidationDispatcher = consolidationDispatcher;
        _consolidationRunStore = consolidationRunStore;
        _dispatchAttemptService = dispatchAttemptService;
        _transitionService = transitionService;
        _agentResolver = agentResolver;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> TryDispatchAsync(
        WorkItemEntity item,
        JobDistributionRequest request,
        AgentId agentId,
        CancellationToken ct)
    {
        // Cancel-during-dispatch race guard: check if run was cancelled while queued
        var runId = request.IssueIdentifier.Value; // RunId stored as IssueIdentifier for consolidation
        var consolidationRun = await _consolidationRunStore.GetByIdAsync((RunId)runId, ct);
        if (consolidationRun is null ||
            consolidationRun.Status == ConsolidationRunStatus.Cancelled ||
            consolidationRun.Status == ConsolidationRunStatus.Failed)
        {
            _logger.LogInformation(
                "ConsolidationDrainDispatcher: consolidation run {RunId} is cancelled/failed, transitioning WorkItem {WorkItemId} to Cancelled",
                runId, item.Id);
            _agentResolver.ReleaseAgent(agentId);
            // DispatchAttemptService has no Cancelled-transition method — call transitionService directly.
            await _transitionService.TransitionAsync(
                item.Id, WorkItemStatus.Cancelled,
                entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct: ct);
            return false;
        }

        try
        {
            // Transition to Dispatched before dispatch attempt.
            // Uses DispatchAttemptService to satisfy the acceptance criterion: no direct
            // _transitionService.TransitionAsync(Dispatched) calls outside dedicated services.
            await _dispatchAttemptService.TransitionToDispatchedAsync(item.Id, agentId, ct);

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
                    "ConsolidationDrainDispatcher: dispatched consolidation WorkItem {WorkItemId} (run {RunId}) to agent {AgentId}",
                    item.Id, runId, agentId);
                return true;
            }
            else
            {
                // Dispatch failed — revert to Pending for next cycle.
                // Passes ct so the caller's cancellation state is respected during the revert.
                // RevertOnFailureAsync (via TryRevertToPendingAsync) swallows any exception internally,
                // so a revert failure during shutdown will not re-enter the catch block below —
                // the stuck-item detector handles items that could not be reverted.
                // NOTE: ReleaseAgent is called before RevertOnFailureAsync. If the revert is cancelled
                // (ct already cancelled during shutdown), the item is left in Dispatched state with the
                // agent already released — the stuck-item detector handles recovery.
                // ReleaseAgent is idempotent (safe to call more than once for the same agentId).
                _agentResolver.ReleaseAgent(agentId);
                await _dispatchAttemptService.RevertOnFailureAsync(item.Id, incrementRetryCount: false, ct: ct);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ConsolidationDrainDispatcher: consolidation dispatch failed for WorkItem {WorkItemId}",
                item.Id);
            _agentResolver.ReleaseAgent(agentId);
            // Revert WorkItem from Dispatched to Pending so it's available for the next drain cycle.
            // Uses CancellationToken.None explicitly: graceful shutdown must not prevent the revert.
            await _dispatchAttemptService.RevertOnFailureAsync(item.Id, incrementRetryCount: false, ct: CancellationToken.None);
            return false;
        }
    }
}
