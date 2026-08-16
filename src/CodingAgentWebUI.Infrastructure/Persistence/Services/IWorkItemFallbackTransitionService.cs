using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Executes the full fallback chain for work item status transitions:
/// direct → two-step (Dispatched→Running→terminal) → infrastructure recovery.
/// </summary>
public interface IWorkItemFallbackTransitionService
{
    /// <summary>
    /// Attempts to transition a work item to the target status using the full fallback chain.
    /// Returns <c>true</c> if any step succeeded, <c>false</c> if all steps were rejected.
    /// </summary>
    /// <param name="workItemId">The work item to transition.</param>
    /// <param name="status">The desired terminal status.</param>
    /// <param name="errorMessage">Optional error message (used when status is Failed).</param>
    /// <param name="failureReason">Optional failure reason (used when status is Failed).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> TryFallbackChainAsync(
        Guid workItemId, WorkItemStatus status,
        string? errorMessage, FailureReason? failureReason,
        CancellationToken ct);
}
