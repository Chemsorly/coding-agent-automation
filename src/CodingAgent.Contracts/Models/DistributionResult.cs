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
/// <param name="IsPermanentFailure">
/// When <c>true</c>, the failure is permanent and will not resolve without a configuration change
/// (e.g., no job template found for the requested agent selector). Callers should cascade the
/// associated work item to a terminal error state rather than leaving it queued for retry.
/// When <c>false</c> (default), the failure is transient (e.g., concurrency limit reached,
/// PVC unavailable) and the item should stay queued for a future retry.
/// Only meaningful when <see cref="Success"/> is <c>false</c>.
/// </param>
public record DistributionResult(bool Success, string? WorkItemId, string? ErrorMessage, bool Queued = false, bool IsPermanentFailure = false);
