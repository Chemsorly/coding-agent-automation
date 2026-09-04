using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Pull request review operations: submit, dismiss, find, and update review comments.
/// Consumed by <c>PostReviewFindingsStep</c>, <c>ExtractLinkedIssuesStep</c>,
/// and <c>WritePrConversationContextStep</c>.
/// </summary>
public interface IPullRequestReviewProvider : IAsyncDisposable
{
    /// <summary>
    /// Whether this provider's platform supports native inline review comments
    /// attached to specific file and line positions in the diff.
    /// Default: false (conservative for providers that have not opted in).
    /// </summary>
    bool SupportsInlineReviewComments => false;

    /// <summary>
    /// Submits a review on a pull request using the platform's native review API.
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support SubmitPullRequestReviewAsync.");

    /// <summary>
    /// Submits a review with optional inline comments. When <see cref="ReviewSubmission.Comments"/>
    /// is empty, produces the same result as the body-only overload.
    /// Default delegates to the body-only overload.
    /// </summary>
    Task SubmitPullRequestReviewAsync(int prNumber, ReviewSubmission submission, CancellationToken ct)
        => SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);

    /// <summary>
    /// Finds and dismisses/resolves previous automated reviews identified by the marker string.
    /// Default is a no-op.
    /// </summary>
    Task DismissPreviousReviewAsync(int prNumber, string marker, string reason, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Searches for an existing review comment containing the specified marker text.
    /// Returns the comment ID if found, null otherwise.
    /// </summary>
    Task<long?> FindExistingReviewCommentAsync(int prNumber, string marker, CancellationToken ct)
        => Task.FromResult<long?>(null);

    /// <summary>
    /// Updates an existing review comment body by its ID.
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task UpdateReviewCommentAsync(int prNumber, long commentId, string body, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support UpdateReviewCommentAsync.");

    /// <summary>
    /// Lists all comments on a pull request including discussion and review thread comments,
    /// for building PR conversation context. Returns comments in chronological order.
    /// Default returns empty.
    /// </summary>
    Task<IReadOnlyList<PrConversationComment>> ListPullRequestCommentsAsync(
        int prNumber, string prAuthor, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PrConversationComment>>(Array.Empty<PrConversationComment>());
}
