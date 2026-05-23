using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Formats code review findings and posts them as a new PR comment.
/// Collapses any previous review comments (identified by marker) into a summary,
/// then posts the new review. This preserves audit history while keeping the PR clean.
/// Non-fatal on posting failure.
/// </summary>
internal sealed class PostReviewFindingsStep : IPipelineStep
{
    private const string NoReviewerMessage =
        "No applicable reviewers found for this repository's labels. Review skipped.";

    private const string SupersededPrefix =
        "<details>\n<summary>⏳ Superseded by newer review (click to expand)</summary>\n\n";

    private const string SupersededSuffix = "\n</details>";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.TransitionTo(PipelineStep.PostingFindings);

        if (!int.TryParse(context.Run.IssueIdentifier, out var prNumber))
        {
            context.Logger.Warning("PR identifier '{Identifier}' is not a valid integer, skipping review posting",
                context.Run.IssueIdentifier);
            return StepResult.Continue;
        }

        // Determine the body: if no reviewers matched, post a different message
        var body = context.Run.CodeReviewAgentsRun.Count == 0
            ? $"{ReviewFindingsFormatter.Marker}\n{NoReviewerMessage}"
            : ReviewFindingsFormatter.Format(context.Run);

        try
        {
            // Collapse any previous review comments so the PR stays clean
            await CollapseExistingReviewsAsync(context, prNumber, ct);

            // Always post a new comment (preserves audit trail)
            await context.RepoProvider.SubmitPullRequestReviewAsync(
                prNumber, body, PullRequestReviewType.Comment, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to post review findings to PR #{PrNumber}", prNumber);
            // Non-fatal: review ran successfully, posting failed
        }

        return StepResult.Continue;
    }

    /// <summary>
    /// Finds all existing review comments (by marker) and collapses them into a
    /// &lt;details&gt; block so they don't clutter the PR but remain accessible.
    /// Best-effort — failures are logged but don't block the new review post.
    /// </summary>
    private static async Task CollapseExistingReviewsAsync(PipelineStepContext context, int prNumber, CancellationToken ct)
    {
        try
        {
            var existingId = await context.RepoProvider.FindExistingReviewCommentAsync(
                prNumber, ReviewFindingsFormatter.Marker, ct);

            while (existingId is not null)
            {
                // Wrap the existing comment body in a <details> collapse.
                // Replace the original marker with a different one so this collapsed comment
                // won't be found again on subsequent runs.
                var collapsedBody = $"<!-- agent:pr-review-superseded -->\n{SupersededPrefix}_This review has been superseded by a newer run below._\n{SupersededSuffix}";
                await context.RepoProvider.UpdateReviewCommentAsync(
                    prNumber, existingId.Value, collapsedBody, ct);

                context.Logger.Debug("Collapsed previous review comment {CommentId} on PR #{PrNumber}",
                    existingId.Value, prNumber);

                // Check for more (in case multiple reviews exist from earlier bugs)
                existingId = await context.RepoProvider.FindExistingReviewCommentAsync(
                    prNumber, ReviewFindingsFormatter.Marker, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to collapse existing review comments on PR #{PrNumber}, continuing with new post", prNumber);
        }
    }
}
