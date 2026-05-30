using Octokit;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    /// <inheritdoc />
    public async Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Use the Pull Request Reviews API so that reviews are dismissible
        // and support inline comments in future overloads.
        var review = new Octokit.PullRequestReviewCreate
        {
            Body = body,
            Event = MapReviewEvent(type)
        };

        await ExecuteWithResilienceAsync(
            client => client.PullRequest.Review.Create(Owner, Repo, prNumber, review),
            "SubmitPullRequestReview", ct);
    }

    /// <inheritdoc />
    public async Task SubmitPullRequestReviewAsync(
        int prNumber, ReviewSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // When Comments is empty, delegate to the existing body-only overload
        // to produce the same observable result.
        if (submission.Comments.Count == 0)
        {
            await SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);
            return;
        }

        // Build the review payload with inline comments using the raw API,
        // because Octokit's DraftPullRequestReviewComment doesn't support 'line' and 'side' fields.
        var comments = submission.Comments.Select(c => new
        {
            path = c.Path,
            line = c.Line,
            side = c.Side == DiffSide.Left ? "LEFT" : "RIGHT",
            body = c.Body
        }).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["body"] = submission.Body,
            ["event"] = MapReviewEventString(submission.Type),
            ["comments"] = comments
        };

        if (submission.CommitId is not null)
        {
            payload["commit_id"] = submission.CommitId;
        }

        try
        {
            await ExecuteWithResilienceAsync(
                async client =>
                {
                    var url = new Uri($"repos/{Owner}/{Repo}/pulls/{prNumber}/reviews", UriKind.Relative);
                    await client.Connection.Post<object>(url, payload, "application/json", null);
                    return true;
                },
                "SubmitPullRequestReviewWithComments", ct);
        }
        catch (ApiValidationException)
        {
            // On HTTP 422, retry once without any comments (body-only fallback).
            // GitHub's 422 response doesn't reliably identify which comment failed.
            Log.Warning(
                "GitHub returned 422 when submitting review with {CommentCount} inline comments on PR #{PrNumber}. " +
                "Retrying with body-only fallback.",
                submission.Comments.Count, prNumber);

            await SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);
        }
    }

    /// <inheritdoc />
    public async Task DismissPreviousReviewAsync(int prNumber, string marker, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(reason);

        // NOTE: This method only finds reviews posted via the Pull Request Reviews API (spec 026+).
        // Old issue-comment-based reviews (from spec 025, pre-migration) are NOT found here —
        // they remain as-is with their <!-- agent:pr-review-superseded --> collapse markers.
        // This is acceptable per the design doc (migration edge case).

        // Get all reviews on the PR. Octokit's GetAll handles pagination automatically.
        var allReviews = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Review.GetAll(Owner, Repo, prNumber),
            "DismissPreviousReview.GetAllReviews", ct);

        // Filter reviews that contain the marker in their body AND are in a dismissible state.
        // GitHub's dismiss API only works on reviews with state CHANGES_REQUESTED or APPROVED.
        // Reviews with state COMMENTED return 422 "Can not dismiss a commented pull request review".
        var matchingReviews = allReviews
            .Where(r => r.Body?.Contains(marker, StringComparison.Ordinal) == true
                        && (r.State.Value == Octokit.PullRequestReviewState.ChangesRequested
                            || r.State.Value == Octokit.PullRequestReviewState.Approved))
            .ToList();

        if (matchingReviews.Count == 0)
        {
            return; // No-op when no matching reviews found.
        }

        Log.Information(
            "Found {Count} previous review(s) to dismiss on PR #{PrNumber}",
            matchingReviews.Count, prNumber);

        // Dismiss each matching review. Log warning and continue on individual failures.
        foreach (var review in matchingReviews)
        {
            try
            {
                await ExecuteWithResilienceAsync(
                    async client =>
                    {
                        var url = new Uri(
                            $"repos/{Owner}/{Repo}/pulls/{prNumber}/reviews/{review.Id}/dismissals",
                            UriKind.Relative);
                        var payload = new { message = reason, @event = "DISMISS" };
                        await client.Connection.Put<object>(url, payload);
                        return true;
                    },
                    $"DismissPreviousReview.Dismiss({review.Id})", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(
                    ex,
                    "Failed to dismiss review {ReviewId} on PR #{PrNumber}. Continuing with remaining reviews.",
                    review.Id, prNumber);
            }
        }
    }

    /// <summary>
    /// Maps the pipeline's <see cref="PullRequestReviewType"/> to the GitHub API event string.
    /// </summary>
    private static string MapReviewEventString(PullRequestReviewType type) => type switch
    {
        PullRequestReviewType.Comment => "COMMENT",
        PullRequestReviewType.RequestChanges => "REQUEST_CHANGES",
        PullRequestReviewType.Approve => "APPROVE",
        _ => "COMMENT"
    };

    /// <summary>
    /// Maps the pipeline's <see cref="PullRequestReviewType"/> to Octokit's <see cref="Octokit.PullRequestReviewEvent"/>.
    /// </summary>
    private static Octokit.PullRequestReviewEvent MapReviewEvent(PullRequestReviewType type) => type switch
    {
        PullRequestReviewType.Comment => Octokit.PullRequestReviewEvent.Comment,
        PullRequestReviewType.RequestChanges => Octokit.PullRequestReviewEvent.RequestChanges,
        PullRequestReviewType.Approve => Octokit.PullRequestReviewEvent.Approve,
        _ => Octokit.PullRequestReviewEvent.Comment
    };

    /// <inheritdoc />
    public async Task<long?> FindExistingReviewCommentAsync(int prNumber, string marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var comments = await ExecuteWithResilienceAsync(
            client => client.Issue.Comment.GetAllForIssue(Owner, Repo, prNumber),
            "FindExistingReviewComment", ct);

        var match = comments.FirstOrDefault(c => c.Body?.Contains(marker, StringComparison.Ordinal) == true);
        return match?.Id;
    }

    /// <inheritdoc />
    public async Task UpdateReviewCommentAsync(int prNumber, long commentId, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        await ExecuteWithResilienceAsync(
            async client =>
            {
                // Use the raw Connection API to avoid Octokit's int limitation on comment IDs.
                // GitHub comment IDs can exceed int.MaxValue on active repositories.
                var url = new Uri($"repos/{Owner}/{Repo}/issues/comments/{commentId}", UriKind.Relative);
                var payload = new { body };
                await client.Connection.Patch<object>(url, payload);
                return true;
            },
            "UpdateReviewComment", ct);
    }
}
