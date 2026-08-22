namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of GET /api/work-items/staleness.
/// Indicates whether a given issue has seen recent agent errors and/or successful completions.
/// </summary>
public sealed record WorkItemStalenessResult
{
    /// <summary>True if any work item for this issue failed with FailureReason.AgentError since the given time.</summary>
    public required bool HasAgentErrorSince { get; init; }

    /// <summary>The timestamp of the last successful completion, or null if there have been none.</summary>
    public required DateTimeOffset? LastSuccessfulCompletion { get; init; }
}
