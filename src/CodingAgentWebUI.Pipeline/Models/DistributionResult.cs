namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of a work distribution attempt via <see cref="Interfaces.IWorkDistributor"/>.
/// </summary>
/// <param name="Success">Whether the work item was successfully distributed.</param>
/// <param name="WorkItemId">The ID of the created work item, if applicable (null in legacy mode).</param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is false.</param>
public record DistributionResult(bool Success, string? WorkItemId, string? ErrorMessage);
