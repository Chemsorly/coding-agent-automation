using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a detected agent-created PR linked to an issue.
/// Carries PR metadata through the rework flow.
/// </summary>
[MessagePackObject]
public sealed class LinkedPullRequest
{
    [Key(0)]
    public required string BranchName { get; init; }

    [Key(1)]
    public required bool IsDraft { get; init; }

    [Key(2)]
    public bool? IsMergeable { get; init; }

    [Key(3)]
    public required int Number { get; init; }

    [Key(4)]
    public IReadOnlyList<PullRequestReviewComment> ReviewComments { get; init; }
        = Array.Empty<PullRequestReviewComment>();

    [Key(5)]
    public required string Url { get; init; }
}
