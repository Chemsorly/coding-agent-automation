using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Formats code review findings and posts them as a PR review comment.
/// Checks for an existing review comment (by marker) and updates it if found,
/// otherwise posts a new review. Non-fatal on posting failure.
/// </summary>
internal sealed class PostReviewFindingsStep : IPipelineStep
{
    private const string NoReviewerMessage =
        "No applicable reviewers found for this repository's labels. Review skipped.";

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
            var existingReviewId = await context.RepoProvider.FindExistingReviewCommentAsync(
                prNumber, ReviewFindingsFormatter.Marker, ct);

            if (existingReviewId is not null)
            {
                await context.RepoProvider.UpdateReviewCommentAsync(
                    prNumber, existingReviewId.Value, body, ct);
            }
            else
            {
                await context.RepoProvider.SubmitPullRequestReviewAsync(
                    prNumber, body, PullRequestReviewType.Comment, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to post review findings to PR #{PrNumber}", prNumber);
            // Non-fatal: review ran successfully, posting failed
        }

        return StepResult.Continue;
    }
}
