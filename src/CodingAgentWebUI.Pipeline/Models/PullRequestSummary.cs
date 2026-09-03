namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Lightweight PR metadata returned by ListOpenPullRequestsAsync.
/// Mirrors IssueSummary conventions (Identifier-based, label-aware).
/// </summary>
public sealed class PullRequestSummary : IHasCreatedAt
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

    /// <summary>
    /// Whether auto-merge is enabled on this PR/MR. When true, the provider will
    /// merge automatically once all required checks pass. Housekeeping prioritises
    /// keeping these branches up-to-date so they can merge without manual intervention.
    /// GitHub: non-null <c>auto_merge</c> field. GitLab: <c>merge_when_pipeline_succeeds</c>.
    /// </summary>
    public bool HasAutoMerge { get; init; }
}
