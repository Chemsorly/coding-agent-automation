namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A complete review submission containing a summary body, review type,
/// and optional inline comments. When Comments is empty, equivalent to body-only review.
/// </summary>
/// <remarks>
/// Immutable sealed record with required init-only properties.
/// An empty <see cref="Comments"/> list represents a body-only review
/// (equivalent to current behavior without inline comments).
/// </remarks>
public sealed record ReviewSubmission
{
    /// <summary>The summary review body (Markdown).</summary>
    public required string Body { get; init; }

    /// <summary>Review type (Comment or RequestChanges).</summary>
    public required PullRequestReviewType Type { get; init; }

    /// <summary>Inline comments to attach to the review.</summary>
    public required IReadOnlyList<ReviewComment> Comments { get; init; }

    /// <summary>
    /// HEAD commit SHA to anchor comments to. Null when commit anchoring is not available
    /// (GitHub defaults to PR HEAD).
    /// </summary>
    public string? CommitId { get; init; }
}
