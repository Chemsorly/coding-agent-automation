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

        // 2. Format the plan comment with marker + approval instructions
        //    If the plan's sub-issue count exceeds the configured cap, prepend a warning.
        var cap = context.Config.MaxDecompositionSubIssues;
        var commentBody = FormatPlanComment(planContent, cap, context.Logger);

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
    /// the plan content and approval instructions. No cap warning is included.
    /// </summary>
    // TODO: int.MaxValue is used as a "no cap" sentinel to suppress the cap warning.
    // This is fragile: if the comparison in FormatPlanComment is ever changed (e.g. to >=),
    // callers using this no-arg overload would silently acquire cap warnings. Consider
    // changing maxSubIssues to int? and short-circuiting on null to make the intent explicit,
    // matching the pattern already used on BuildReviewPrompt and BuildRefinementPrompt.
    internal static string FormatPlanComment(string planContent)
        => FormatPlanComment(planContent, int.MaxValue, null);

    /// <summary>
    /// Formats the plan comment with the marker as the first line, followed by
    /// the plan content and approval instructions.
    /// When the plan's sub-issue table contains more rows than <paramref name="maxSubIssues"/>,
    /// prepends a visible warning. On parse failure, posts without warning (fail-open).
    /// </summary>
    internal static string FormatPlanComment(string planContent, int maxSubIssues, Serilog.ILogger? logger = null)
    {
        var sb = new System.Text.StringBuilder();

        // Marker MUST be first line
        sb.AppendLine(CommentMarkers.DecompositionPlan);
        sb.AppendLine();

        // Attempt to count sub-issues and warn if over cap
        var countResult = TryCountSubIssuesInPlan(planContent, logger);
        if (countResult.HasValue && countResult.Value > maxSubIssues)
        {
            sb.AppendLine($"> ⚠️ **Cap warning:** This plan proposes **{countResult.Value} sub-issues** but the configured maximum is **{maxSubIssues}**.");
            sb.AppendLine($"> Only the first **{maxSubIssues}** sub-issues (by filename order) would be created if approved.");
            sb.AppendLine($"> Review the plan and request changes to reduce the count before approving.");
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
    /// Attempts to count data rows in the sub-issue table of a decomposition plan.
    /// The table is identified as the first markdown table whose header row contains
    /// both a <c>#</c> column and a <c>Title</c> column.
    /// Returns null when no such table is found or on any parse error (fail-open).
    /// </summary>
    internal static int? TryCountSubIssuesInPlan(string planContent, Serilog.ILogger? logger = null)
    {
        try
        {
            // Split into lines and scan for the sub-issue table header.
            // A header row looks like: | # | Title | ... |
            // The separator row immediately follows: |---|---|...|
            var lines = planContent.Split('\n');
            var headerIndex = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (IsSubIssueTableHeader(line))
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
                return null;

            // Count data rows immediately after the separator line
            // (skip blank lines or lines not starting/ending with '|')
            var dataRowCount = 0;
            var separatorFound = false;

            for (var i = headerIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (!separatorFound)
                {
                    // The first line after the header should be the separator row (|---|...|)
                    if (IsMarkdownTableSeparator(line))
                    {
                        separatorFound = true;
                        continue;
                    }
                    // If the line after the header is not a separator, this is not a valid table
                    return null;
                }

                // After separator: count non-empty pipe-delimited rows
                // TODO: A blank line embedded inside the table body terminates the count early here,
                // causing an undercount and suppressing the cap warning even when the plan exceeds the cap.
                // The issue spec defines fail-open as "can't parse the table → no warning", but this
                // silently miscounts a partially-parsable table instead of returning null. Consider
                // skipping blank lines (continue) rather than breaking on the first non-pipe line,
                // or returning null when a blank line is encountered inside the table body.
                if (line.StartsWith('|') && line.EndsWith('|'))
                    dataRowCount++;
                else
                    break; // Table ended
            }

            return separatorFound ? dataRowCount : null;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Failed to parse sub-issue table in decomposition plan; posting without cap warning");
            return null;
        }
    }

    /// <summary>
    /// Returns true if the line is a markdown table header row that contains
    /// both a <c>#</c> column and a <c>Title</c> column.
    /// </summary>
    private static bool IsSubIssueTableHeader(string line)
    {
        if (!line.StartsWith('|') || !line.EndsWith('|'))
            return false;

        // Split on '|', trim each cell
        var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hasNumberColumn = false;
        var hasTitleColumn = false;
        foreach (var cell in cells)
        {
            if (cell == "#") hasNumberColumn = true;
            if (cell.Equals("Title", StringComparison.OrdinalIgnoreCase)) hasTitleColumn = true;
        }

        return hasNumberColumn && hasTitleColumn;
    }

    /// <summary>
    /// Returns true if the line is a markdown table separator row (contains only <c>|</c>, <c>-</c>, <c>:</c>, and spaces).
    /// </summary>
    private static bool IsMarkdownTableSeparator(string line)
    {
        if (!line.StartsWith('|') || !line.EndsWith('|'))
            return false;

        foreach (var c in line)
        {
            if (c != '|' && c != '-' && c != ':' && c != ' ')
                return false;
        }

        return line.Contains('-');
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
