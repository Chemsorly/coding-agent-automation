using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Posts a summary comment on the epic issue listing created and failed sub-issues,
/// then swaps the label to <c>agent:done</c> (partial/full success) or <c>agent:error</c>
/// (all creations failed or zero sub-issues attempted).
/// 
/// Summary post failure is non-fatal: the step logs the error and proceeds with the label swap.
/// </summary>
internal sealed class PostDecompositionSummaryStep : IPipelineStep
{
    public string StepName => "PostDecompositionSummary";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("PostDecompositionSummary");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        context.Callbacks.TransitionTo(PipelineStep.PostingSummary);

        var results = context.Run.SubIssueResults;

        // Determine outcome: zero attempted or all failed → error; otherwise → done
        var attempted = results.Count;
        var succeeded = results.Count(r => r.Success);
        var failed = attempted - succeeded;
        var allFailed = attempted == 0 || succeeded == 0;

        var targetLabel = allFailed ? AgentLabels.Error : AgentLabels.Done;

        // Format summary comment
        var summaryBody = FormatSummaryComment(results, attempted, succeeded, failed);

        // Post summary comment (non-fatal on failure)
        try
        {
            await context.IssueOps.PostCommentAsync(context.Run.IssueIdentifier, summaryBody, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Failed to post decomposition summary comment on issue {IssueId}, proceeding with label swap",
                context.Run.IssueIdentifier);
        }

        // Swap label (non-fatal on failure — run will complete without label transition)
        context.Run.FinalLabel = targetLabel;
        try
        {
            await context.IssueOps.SwapLabelAsync(context.Run.IssueIdentifier, targetLabel, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Failed to swap label to {Label} on issue {IssueId}, run will complete without label transition",
                targetLabel, context.Run.IssueIdentifier);
        }

        if (allFailed)
        {
            var reason = attempted == 0
                ? "Zero sub-issues attempted"
                : "All sub-issue creations failed";
            context.Logger.Warning("{Reason} for epic {IssueId}", reason, context.Run.IssueIdentifier);
        }
        else
        {
            context.Logger.Information(
                "Decomposition complete for epic {IssueId}: {Succeeded}/{Attempted} sub-issues created",
                context.Run.IssueIdentifier, succeeded, attempted);
        }

        return StepResult.Continue;
    }

    /// <summary>
    /// Formats the summary comment with the decomposition-summary marker and a results table.
    /// </summary>
    internal static string FormatSummaryComment(
        IReadOnlyList<SubIssueCreationResult> results, int attempted, int succeeded, int failed)
    {
        var sb = new System.Text.StringBuilder();

        // Marker must be first line
        sb.AppendLine(CommentMarkers.DecompositionSummary);
        sb.AppendLine();
        sb.AppendLine("## 🧩 Decomposition Summary");
        sb.AppendLine();

        if (attempted == 0)
        {
            sb.AppendLine("⚠️ No sub-issues were attempted.");
            return sb.ToString();
        }

        sb.AppendLine($"**Created:** {succeeded}/{attempted} sub-issues");
        if (failed > 0)
            sb.AppendLine($"**Failed:** {failed}/{attempted} sub-issues");
        sb.AppendLine();

        // Results table
        sb.AppendLine("| # | Title | Status | Link |");
        sb.AppendLine("|---|-------|--------|------|");

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var status = result.Success ? "✅ Created" : "❌ Failed";
            var link = result.Success && result.Url is not null
                ? $"[#{result.Identifier}]({result.Url})"
                : result.FailureReason ?? "Unknown error";

            var sanitizedTitle = TextSanitizer.SanitizeMarkdown(result.Title);
            sb.AppendLine($"| {i + 1} | {EscapeMarkdownPipe(sanitizedTitle)} | {status} | {link} |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes pipe characters in text to prevent breaking markdown table formatting.
    /// </summary>
    private static string EscapeMarkdownPipe(string text)
        => text.Replace("|", "\\|");
}
