using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Posts (or updates) the decomposition plan comment on the epic issue, then swaps
/// the label to <c>agent:epic-review</c> for human approval.
///
/// On re-run, identifies the existing plan comment by the
/// <see cref="CommentMarkers.DecompositionPlan"/> marker (most recent match)
/// and updates it in place to avoid duplicate comments.
///
/// On posting failure: sets error on context and returns <see cref="StepResult.Stop"/>.
/// </summary>
internal sealed class PostDecompositionPlanStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("PostDecompositionPlan");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        context.Callbacks.TransitionTo(PipelineStep.PostingPlan);

        // 1. Read the decomposition plan from workspace
        var planPath = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.DecompositionPlanFilePath);

        if (!File.Exists(planPath))
        {
            await context.FailRunAsync("Decomposition plan file not found at workspace path", ct);
            return StepResult.Stop;
        }

        var planContent = await File.ReadAllTextAsync(planPath, ct);

        if (string.IsNullOrWhiteSpace(planContent) || planContent.Length < 20)
        {
            await context.FailRunAsync("Decomposition plan file is empty or too short (< 20 characters)", ct);
            return StepResult.Stop;
        }

        // 2. Format the plan comment with marker + approval instructions
        var commentBody = FormatPlanComment(planContent);

        // 3. Check for existing plan comment (most recent with marker)
        var postResult = await context.TryCriticalAsync(async () =>
        {
            var comments = await context.IssueOps.ListCommentsAsync(context.Run.IssueIdentifier, ct);
            var existingComment = FindMostRecentPlanComment(comments);

            if (existingComment is not null)
            {
                // Update existing comment to avoid duplicates
                await context.IssueOps.UpdateCommentAsync(
                    context.Run.IssueIdentifier, existingComment.Id, commentBody, ct);
                context.Logger.Information(
                    "Updated existing decomposition plan comment {CommentId} on issue {IssueId}",
                    existingComment.Id, context.Run.IssueIdentifier);
            }
            else
            {
                // Post new comment
                await context.IssueOps.PostCommentAsync(context.Run.IssueIdentifier, commentBody, ct);
                context.Logger.Information(
                    "Posted new decomposition plan comment on issue {IssueId}",
                    context.Run.IssueIdentifier);
            }
        }, "Post decomposition plan comment", ct);

        if (postResult == StepResult.Stop)
            return StepResult.Stop;

        // 4. Swap label to agent:epic-review
        await context.IssueOps.SwapLabelAsync(context.Run.IssueIdentifier, AgentLabels.EpicReview, ct);
        context.Run.FinalLabel = AgentLabels.EpicReview;
        context.Logger.Information(
            "Swapped label to {Label} on issue {IssueId}",
            AgentLabels.EpicReview, context.Run.IssueIdentifier);

        return StepResult.Continue;
    }

    /// <summary>
    /// Formats the plan comment with the marker as the first line, followed by
    /// the plan content and approval instructions.
    /// </summary>
    internal static string FormatPlanComment(string planContent)
    {
        var sb = new System.Text.StringBuilder();

        // Marker MUST be first line
        sb.AppendLine(CommentMarkers.DecompositionPlan);
        sb.AppendLine();
        sb.AppendLine("## 🧩 Decomposition Plan");
        sb.AppendLine();
        sb.AppendLine(TextSanitizer.SanitizeMarkdown(planContent));
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### ✅ Approval Instructions");
        sb.AppendLine();
        sb.AppendLine("To approve this plan and proceed with sub-issue creation:");
        sb.AppendLine("1. Review the proposed sub-issues above");
        sb.AppendLine("2. Remove the `agent:epic-review` label");
        sb.AppendLine("3. Add the `agent:epic-approved` label");
        sb.AppendLine();
        sb.AppendLine("To request changes:");
        sb.AppendLine("1. Post a comment with your feedback");
        sb.AppendLine("2. Remove the `agent:epic-review` label");
        sb.AppendLine("3. Add the `agent:epic` label to trigger re-analysis");

        return sb.ToString();
    }

    /// <summary>
    /// Finds the most recent comment containing the decomposition plan marker.
    /// Returns null if no matching comment exists.
    /// </summary>
    internal static IssueComment? FindMostRecentPlanComment(IReadOnlyList<IssueComment> comments)
    {
        // Search from most recent to oldest
        for (var i = comments.Count - 1; i >= 0; i--)
        {
            if (comments[i].Body.Contains(CommentMarkers.DecompositionPlan, StringComparison.Ordinal))
                return comments[i];
        }

        return null;
    }
}
