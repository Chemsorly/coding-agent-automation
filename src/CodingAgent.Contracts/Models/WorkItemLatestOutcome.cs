namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Result of GET /api/work-items/latest-outcome.
/// Holds the status and optional final label of the most recently created work item for an issue.
/// Used by the orphaned-label recovery sweep to distinguish a silently-succeeded run (hub
/// completion report was lost) from a genuinely dead one.
/// </summary>
public sealed record WorkItemLatestOutcome
{
    /// <summary>The status of the most recently created work item.</summary>
    public required WorkItemStatus Status { get; init; }

    /// <summary>
    /// The final label from the work item's completion payload, or null when the work item has no
    /// result payload, the payload is malformed, or the payload carries no FinalLabel.
    /// </summary>
    public string? FinalLabel { get; init; }
}
