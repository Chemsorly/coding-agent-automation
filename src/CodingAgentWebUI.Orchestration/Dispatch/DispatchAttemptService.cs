using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Encapsulates the shared dispatch-attempt lifecycle for the SignalR (DB+SignalR) dispatch mode:
/// <list type="number">
///   <item><description>Transition the <see cref="WorkItemEntity"/> to <see cref="WorkItemStatus.Dispatched"/>,
///   setting <c>DispatchedAt</c> and <c>AssignedAgentId</c>.</description></item>
///   <item><description>Revert to <see cref="WorkItemStatus.Pending"/> on failure, delegating to
///   <see cref="DispatchRevertHandler.TryRevertToPendingAsync"/>.</description></item>
/// </list>
///
/// <para>
/// <b>SignalR-only:</b> This service is intentionally scoped to the SignalR dispatch path.
/// The K8s path (<see cref="DispatchLifecycleService"/>) uses a different ordering
/// (transition-to-Dispatched happens <em>after</em> K8s Job creation, not before) and does not
/// populate <c>AssignedAgentId</c> (agent identity is tracked via <c>K8sJobName</c> instead).
/// Do not call <see cref="TransitionToDispatchedAsync"/> from K8s dispatch code.
/// </para>
///
/// <para>
/// Extracted from <see cref="PendingWorkItemDrainService"/> to eliminate duplicate
/// transition + revert logic between <c>DispatchPipelineItemAsync</c> and
/// <c>DispatchConsolidationItemAsync</c> (issue #1914).
/// </para>
/// </summary>
public sealed class DispatchAttemptService
{
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchRevertHandler _revertHandler;

    public DispatchAttemptService(
        WorkItemTransitionService transitionService,
        DispatchRevertHandler revertHandler)
    {
        ArgumentNullException.ThrowIfNull(transitionService);
        ArgumentNullException.ThrowIfNull(revertHandler);
        _transitionService = transitionService;
        _revertHandler = revertHandler;
    }

    /// <summary>
    /// Transitions the work item to <see cref="WorkItemStatus.Dispatched"/>, setting
    /// <c>DispatchedAt = UtcNow</c> and <c>AssignedAgentId = agentId.Value</c>.
    /// Propagates any exception from the underlying <see cref="WorkItemTransitionService"/>
    /// (e.g., <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>) so that
    /// callers can handle the <c>dispatchedSuccessfully</c> flag correctly.
    /// </summary>
    /// <param name="workItemId">The work item to transition.</param>
    /// <param name="agentId">The agent assigned to the work item.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task TransitionToDispatchedAsync(Guid workItemId, AgentId agentId, CancellationToken ct) =>
        _transitionService.TransitionAsync(
            workItemId,
            WorkItemStatus.Dispatched,
            entity =>
            {
                entity.DispatchedAt = DateTimeOffset.UtcNow;
                entity.AssignedAgentId = agentId.Value;
            },
            ct: ct);

    /// <summary>
    /// Reverts a work item from <see cref="WorkItemStatus.Dispatched"/> back to
    /// <see cref="WorkItemStatus.Pending"/> after a dispatch failure. Swallows exceptions
    /// internally (via <see cref="DispatchRevertHandler.TryRevertToPendingAsync"/>) — the
    /// stuck-item detector handles items that could not be reverted.
    /// </summary>
    /// <param name="workItemId">The work item to revert.</param>
    /// <param name="incrementRetryCount">
    /// <c>true</c> for pipeline dispatch failures (RetryCount must increment);
    /// <c>false</c> for consolidation dispatch failures.
    /// </param>
    /// <param name="ct">
    /// Pass the caller's token when the revert should respect cancellation (e.g., false-return
    /// path in <c>DispatchConsolidationItemAsync</c>). Pass
    /// <see cref="CancellationToken.None"/> in catch blocks so that graceful shutdown does not
    /// prevent the revert from completing.
    /// </param>
    public Task RevertOnFailureAsync(Guid workItemId, bool incrementRetryCount, CancellationToken ct) =>
        _revertHandler.TryRevertToPendingAsync(workItemId, incrementRetryCount, ct);
}
