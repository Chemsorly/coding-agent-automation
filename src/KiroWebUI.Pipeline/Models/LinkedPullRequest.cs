namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Represents a detected agent-created PR linked to an issue.
/// Carries PR metadata through the rework flow.
/// </summary>
public sealed class LinkedPullRequest
{
    public required int Number { get; init; }
    public required string BranchName { get; init; }
    public required string Url { get; init; }
    public required bool IsDraft { get; init; }
    public bool? IsMergeable { get; init; }
    public IReadOnlyList<PullRequestReviewComment> ReviewComments { get; init; }
        = Array.Empty<PullRequestReviewComment>();
}
