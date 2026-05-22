using System.Text;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Formats code review findings from a pipeline run into a Markdown review body
/// suitable for posting as a PR review comment.
/// </summary>
internal static class ReviewFindingsFormatter
{
    internal const string Marker = "<!-- agent:pr-review -->";

    public static string Format(PipelineRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Marker);
        sb.AppendLine("## 🤖 Automated Code Review");
        sb.AppendLine();

        if (run.CodeReviewAgentsRun.Count > 0)
            sb.AppendLine($"**Review Agents**: {string.Join(", ", run.CodeReviewAgentsRun)}");
        sb.AppendLine();

        // Summary table
        if (run.CodeReviewCriticalCount == 0 && run.CodeReviewWarningCount == 0
            && run.CodeReviewSuggestionCount == 0)
        {
            sb.AppendLine("✅ No issues found.");
        }
        else
        {
            sb.AppendLine("| Severity | Count |");
            sb.AppendLine("|----------|-------|");
            if (run.CodeReviewCriticalCount > 0)
                sb.AppendLine($"| [CRITICAL] | {run.CodeReviewCriticalCount} |");
            if (run.CodeReviewWarningCount > 0)
                sb.AppendLine($"| [WARNING] | {run.CodeReviewWarningCount} |");
            if (run.CodeReviewSuggestionCount > 0)
                sb.AppendLine($"| [SUGGESTION] | {run.CodeReviewSuggestionCount} |");
        }
        sb.AppendLine();

        // Per-agent findings
        foreach (var kvp in run.CodeReviewAgentFindings)
        {
            if (string.IsNullOrEmpty(kvp.Value)) continue;
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{kvp.Key}</summary>");
            sb.AppendLine();
            sb.AppendLine(kvp.Value);
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
