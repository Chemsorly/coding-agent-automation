using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Exposes the atomic compare-and-swap transition primitive on a work item.
/// Implemented by <see cref="WorkItemTransitionService"/>.
/// </summary>
public interface IWorkItemTransitionService
{
    /// <summary>
    /// Atomic compare-and-swap transition.
    /// Succeeds only if the current status matches <paramref name="expectedCurrent"/>.
    /// Never idempotent — returns false when current == target regardless of expectedCurrent.
    /// Two concurrent callers with the same expectedCurrent will see exactly one succeed.
    /// </summary>
    /// <param name="workItemId">The work item to transition.</param>
    /// <param name="expectedCurrent">The status the row must currently have for the transition to apply.</param>
    /// <param name="target">The desired target status.</param>
    /// <param name="mutate">Optional action to set additional fields on success (e.g., AssignedAgentId, DispatchedAt).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> TransitionIfAsync(
        Guid workItemId,
        WorkItemStatus expectedCurrent,
        WorkItemStatus target,
        Action<WorkItemEntity>? mutate = null,
        CancellationToken ct = default);
}
