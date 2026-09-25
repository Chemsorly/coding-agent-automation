using System.Diagnostics;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

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
public sealed class PostDecompositionPlanStep : IPipelineStep
{
    public string StepName => "PostDecompositionPlan";

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

        // 2a. Check whether the plan's sub-issue table exceeds the cap and emit a warning if so.
        string? warningPreamble = null;
        var cap = context.Config.MaxDecompositionSubIssues;
        var subIssueCount = TryCountPlanSubIssues(planContent);
        if (subIssueCount.HasValue)
        {
            if (subIssueCount.Value > cap)
            {
                warningPreamble =
                    $"> ⚠️ **Warning: this plan contains {subIssueCount.Value} sub-issues but the cap is {cap}.**\n" +
                    $"> Only the first {cap} sub-issues will be created when this plan is approved.\n" +
                    $"> Please revise the plan so it proposes at most {cap} sub-issues before approving.";
            }
        }
        else
        {
            context.Logger.Information(
                "Could not parse sub-issue table in decomposition plan for run {RunId} — skipping cap warning",
                context.Run.RunId);
        }

        // 2. Format the plan comment with marker + approval instructions
        var commentBody = FormatPlanComment(planContent, warningPreamble);

        // 3. Check for existing plan comment (most recent with marker)
        var postResult = await context.TryCriticalAsync(async () =>
        {
            var comments = await context.IssueOps.ListCommentsAsync(context.Run.IssueIdentifier, ct);
            var existingComment = FindMostRecentPlanComment(comments);

            if (existingComment is not null)
            {
                // Update existing comment to avoid duplicates
                // TODO: long.Parse throws FormatException if a provider returns a non-numeric comment ID.
                // Inside TryCriticalAsync this will abort the pipeline step. Use long.TryParse with a
                // descriptive error once non-GitHub/GitLab providers are introduced.
                // See review finding on PostDecompositionPlanStep.cs:60.
                await context.IssueOps.UpdateCommentAsync(
                    context.Run.IssueIdentifier, long.Parse(existingComment.Id), commentBody, ct);
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

        // 4. Swap label to agent:epic-review (non-fatal on failure — run will complete without label transition)
        context.Run.FinalLabel = AgentLabels.EpicReview;
        try
        {
            await context.IssueOps.SwapLabelAsync(context.Run.IssueIdentifier, AgentLabels.EpicReview, ct);
            context.Logger.Information(
                "Swapped label to {Label} on issue {IssueId}",
                AgentLabels.EpicReview, context.Run.IssueIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex, ct);
            context.Logger.Error(ex,
                "Failed to swap label to {Label} on issue {IssueId}, run will complete without label transition",
                AgentLabels.EpicReview, context.Run.IssueIdentifier);
        }

        return StepResult.Continue;
    }

    /// <summary>
    /// Formats the plan comment with the marker as the first line, followed by
    /// an optional warning preamble, the plan content, and approval instructions.
    /// </summary>
    internal static string FormatPlanComment(string planContent, string? warningPreamble = null)
    {
        var sb = new System.Text.StringBuilder();

        // Marker MUST be first line
        sb.AppendLine(CommentMarkers.DecompositionPlan);
        sb.AppendLine();

        if (warningPreamble is not null)
        {
            sb.AppendLine(warningPreamble);
            sb.AppendLine();
        }

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
    /// Counts the data rows in the first markdown table whose header row contains
    /// both a <c>#</c> column and a <c>Title</c> column (the structure mandated by the
    /// decomposition analysis prompt).
    /// Returns <c>null</c> when no matching table is found (fail-open; caller proceeds without warning).
    /// </summary>
    internal static int? TryCountPlanSubIssues(string planContent)
    {
        // TODO: planContent.Split('\n') does not normalise CRLF line endings. On Windows or when the plan
        // file is written with CRLF endings each line carries a trailing '\r', which can cause blank/'\r'-only
        // lines to be misidentified as empty rows and terminate counting early, potentially under-counting
        // sub-issues mid-table. Follow the established pattern in PullRequestFinalizationService (ReplaceLineEndings("\n"))
        // and replace with: planContent.ReplaceLineEndings("\n").Split('\n')
        var lines = planContent.Split('\n');
        var headerIndex = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.TrimStart().StartsWith('|'))
                continue;

            // Split by '|', trim each token, ignore empty tokens at edges
            var tokens = line.Split('|')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToArray();

            // Must have both '#' and 'Title' as column header tokens (case-insensitive)
            var hasHash = tokens.Any(t => string.Equals(t, "#", StringComparison.OrdinalIgnoreCase));
            var hasTitle = tokens.Any(t => string.Equals(t, "Title", StringComparison.OrdinalIgnoreCase));

            if (hasHash && hasTitle)
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
            return null;

        // Skip the separator row (contains |---|)
        var dataStart = headerIndex + 1;
        if (dataStart < lines.Length && lines[dataStart].Contains("---"))
            dataStart++;

        // Count data rows: lines starting with '|' that are not separator rows
        var count = 0;
        for (var i = dataStart; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                break;
            if (!line.TrimStart().StartsWith('|'))
                break;
            // Skip separator rows (e.g. |---|---|)
            // TODO: line.Contains("---") is a substring match that will also match data rows whose cell
            // content includes three or more hyphens (e.g. "N/A --- see below", "YYYY-MM-DD---format").
            // Such rows are silently skipped, causing an undercount and potentially suppressing the cap
            // warning. Use a structural separator check instead: verify that every non-empty token between
            // pipes consists only of '-', ':', and whitespace (i.e. a real markdown separator row).
            if (line.Contains("---"))
                continue;
            count++;
        }

        return count;
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
