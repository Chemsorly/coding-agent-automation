namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Result of a work distribution attempt via <see cref="Interfaces.IWorkDistributor"/>.
/// </summary>
/// <param name="Success">Whether the work item was successfully distributed.</param>
/// <param name="WorkItemId">The ID of the created work item, or <c>null</c> if not applicable.</param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is false.</param>
/// <param name="Queued">
/// When <c>true</c>, the work item was queued as Pending (no idle agent available) rather than
/// immediately dispatched to an agent. Callers should NOT swap the issue label to
/// <c>agent:in-progress</c> — the label swap happens later when the drain service assigns it.
/// </param>
/// <param name="Permanent">
/// When <c>true</c> and <paramref name="Success"/> is <c>false</c>, the failure is permanent
/// (e.g. no job template for the resolved agent selector) and will not be resolved by retrying.
/// Callers should cascade the item to a terminal failure state (e.g. <c>Failed</c>) rather than
/// leaving it <c>Queued</c> for the next restart-rehydration attempt.
/// When <c>false</c> and <paramref name="Success"/> is <c>false</c>, the failure is transient
/// (e.g. concurrency limit, PVC unavailable) and the item should remain <c>Queued</c>.
/// </param>
public record DistributionResult(
    bool Success,
    string? WorkItemId,
    string? ErrorMessage,
    bool Queued = false,
    bool Permanent = false);
