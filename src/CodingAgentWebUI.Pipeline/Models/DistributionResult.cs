namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of a work distribution attempt via <see cref="Interfaces.IWorkDistributor"/>.
/// </summary>
/// <param name="Success">Whether the work item was successfully distributed.</param>
/// <param name="WorkItemId">The ID of the created work item, if applicable (null in legacy mode).</param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is false.</param>
/// <param name="Queued">
/// When <c>true</c>, the work item was queued as Pending (no idle agent available) rather than
/// immediately dispatched to an agent. Callers should NOT swap the issue label to
/// <c>agent:in-progress</c> — the label swap happens later when the drain service assigns it.
/// </param>
public record DistributionResult(bool Success, string? WorkItemId, string? ErrorMessage, bool Queued = false);
