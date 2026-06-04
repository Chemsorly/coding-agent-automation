namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Lightweight PR metadata returned by ListOpenPullRequestsAsync.
/// Mirrors IssueSummary conventions (Identifier-based, label-aware).
/// </summary>
public sealed class PullRequestSummary
{
    public required int Number { get; init; }
    public required string Identifier { get; init; }  // PR number as string
    public required string Title { get; init; }
    public required string Description { get; init; } // PR body text
    public required IReadOnlyList<string> Labels { get; init; }
    public required string BranchName { get; init; }
    public required string TargetBranch { get; init; } // e.g., "main", "develop"
    public required string Url { get; init; }
    public required bool IsDraft { get; init; }

    /// <summary>PR author username (e.g., GitHub login or GitLab username).</summary>
    public string? Author { get; init; }

    /// <summary>PR creation date, used for FIFO ordering in the pipeline loop.</summary>
    public DateTime? CreatedAt { get; init; }
}
