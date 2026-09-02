namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discriminated result of a <see cref="CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService.TransitionDetailedAsync"/> call.
/// Allows callers to distinguish a genuine state change from an idempotent no-op or a rejection,
/// without requiring an additional database round-trip.
/// </summary>
public enum TransitionResult
{
    /// <summary>The status actually changed and the DB write was committed.</summary>
    Transitioned,

    /// <summary>The item was already at the target status — idempotent no-op, no DB write occurred.</summary>
    AlreadyAtTarget,

    /// <summary>The requested transition is not valid from the item's current state, or all concurrency retries were exhausted.</summary>
    Rejected,

    /// <summary>No work item with the given ID exists in the database.</summary>
    NotFound,
}
