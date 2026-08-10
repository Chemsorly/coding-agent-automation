using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Handles pipeline dispatch failure recovery: agent release, in-memory run cleanup,
/// work item revert to Pending, and RetryCount management.
/// Also manages in-memory PipelineRun registration at dispatch time.
/// Extracted from <see cref="PendingWorkItemDrainService"/> to reduce its size (#1871).
/// </summary>
public sealed class DispatchRevertService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ISignalRWorkDistributorAgentResolver _agentResolver;
    private readonly IOrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ILogger<DispatchRevertService> _logger;

    public DispatchRevertService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ISignalRWorkDistributorAgentResolver agentResolver,
        IOrchestratorRunService runService,
        WorkItemTransitionService transitionService,
        ILogger<DispatchRevertService> logger)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for all five parameters to match the
        // constructor guard pattern used by every other service in this directory
        // (ConsolidationWorkItemDispatchService, AgentJobDispatcher, etc.). Without this, a null
        // dependency produces a NullReferenceException deep inside dispatch/revert logic
        // rather than at construction time, making root cause diagnosis harder. (#1871)
        _dbFactory = dbFactory;
        _agentResolver = agentResolver;
        _runService = runService;
        _transitionService = transitionService;
        _logger = logger;
    }

    /// <summary>
    /// Updates or re-creates the in-memory <see cref="PipelineRun"/> after the DB transition to
    /// Dispatched has succeeded. Handles the orchestrator-restart recovery case where the run was
    /// lost from memory and must be re-created from the distribution request.
    /// </summary>
    public void EnsureInMemoryRunRegistered(
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
                "DispatchRevertService: re-created in-memory PipelineRun {RunId} for issue {IssueIdentifier} (orchestrator restart recovery)",
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
    public async Task HandlePipelineDispatchFailureAsync(
        WorkItemEntity item,
        JobDistributionRequest request,
        AgentId agentId,
        bool dispatchedSuccessfully,
        Exception ex)
    {
        _logger.LogError(ex,
            "DispatchRevertService: dispatch failed for WorkItem {WorkItemId}",
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
        // TODO: Double-increment risk: TryRevertToPendingAsync(incrementRetryCount: true) above
        // relies on WorkItemTransitionService.TransitionAsync skipping the mutation callback when
        // the item is already in the target state (Pending → Pending idempotent no-op). If that
        // behaviour ever changes and the callback fires on a no-op transition, RetryCount would be
        // incremented twice for the same failure — once via the callback and once via the direct
        // DB update below. This assumption is not enforced here. Add a guard or audit
        // TransitionAsync idempotency if the retry ceiling is hit prematurely. (#1871)
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
                    "DispatchRevertService: failed to increment RetryCount for WorkItem {WorkItemId} after dispatch-transition failure",
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
    public async Task TryRevertToPendingAsync(Guid workItemId, bool incrementRetryCount,
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
                "DispatchRevertService: failed to revert WorkItem {WorkItemId} to Pending after dispatch failure — stuck-item detector will handle",
                workItemId);
        }
    }

}
