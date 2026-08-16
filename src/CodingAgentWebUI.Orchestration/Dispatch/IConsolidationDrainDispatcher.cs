using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Dispatches a consolidation work item from the drain loop to an agent.
/// Encapsulates the cancel-during-dispatch guard, DB transitions (Dispatched and Cancelled),
/// and revert-to-Pending on failure. Implemented by <see cref="ConsolidationDrainDispatcher"/>.
/// </summary>
/// <remarks>
/// Defined in the <c>CodingAgentWebUI.Orchestration.Dispatch</c> namespace rather than
/// <c>CodingAgentWebUI.Pipeline.Interfaces</c> to avoid a layering violation:
/// <see cref="WorkItemEntity"/> is in <c>Infrastructure.Persistence.Entities</c> and must not
/// be referenced from the shared Pipeline library.
/// </remarks>
public interface IConsolidationDrainDispatcher
{
    /// <summary>
    /// Attempts to dispatch the consolidation work item to the specified agent.
    /// Handles DB state transitions, run cancellation guard, and revert on failure.
    /// Returns <c>true</c> if the item was successfully dispatched; <c>false</c> in all other cases
    /// (cancelled run, dispatch failure, or exception).
    /// </summary>
    Task<bool> TryDispatchAsync(
        WorkItemEntity item,
        JobDistributionRequest request,
        AgentId agentId,
        CancellationToken ct);
}
