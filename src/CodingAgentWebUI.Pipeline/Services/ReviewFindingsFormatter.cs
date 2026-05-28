using System.Text;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Formats code review findings from a pipeline run into a Markdown review body
/// suitable for posting as a PR review comment.
/// </summary>
internal static class ReviewFindingsFormatter
{
    public static string Format(PipelineRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CommentMarkers.PrReview);
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

        // Inline comment status (only when inline comments are active)
        if (run.InlineCommentsPosted > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine($"📍 **{run.InlineCommentsPosted} finding(s) posted as inline comments.**");
            sb.AppendLine();
        }

        // Degradation notice (when fallback to body-only occurred)
        if (run.InlineCommentsDegraded)
        {
            var reason = run.InlineCommentsDegradedReason ?? "Unknown reason";
            sb.AppendLine($"⚠️ Inline comments degraded: {reason}. Findings included in body only.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a "Findings by Location" section for use when the provider doesn't support
    /// inline comments but findings have location metadata. Findings are grouped by file path
    /// alphabetically, with each finding as a bullet point showing severity emoji, line number,
    /// and message. Findings within each file are ordered by line number.
    /// </summary>
    /// <param name="findings">Structured findings with non-null FilePath and LineNumber > 0.</param>
    /// <returns>
    /// A Markdown string containing the "Findings by Location" section, or an empty string
    /// if no findings with location data are provided.
    /// </returns>
    public static string FormatFindingsByLocation(IReadOnlyList<StructuredFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        // Filter to only findings with location metadata
        var withLocation = findings
            .Where(f => f.FilePath is not null && f.LineNumber > 0)
            .ToList();

        if (withLocation.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("### 📍 Findings by Location");
        sb.AppendLine();

        // Group by file path alphabetically
        var grouped = withLocation
            .GroupBy(f => f.FilePath!)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            sb.AppendLine($"#### {group.Key}");

            // Order findings within each file by line number
            foreach (var finding in group.OrderBy(f => f.LineNumber))
            {
                var emoji = GetSeverityEmoji(finding.Severity);
                sb.AppendLine($"- {emoji} Line {finding.LineNumber}: {finding.Message}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the severity emoji for a given finding severity level.
    /// </summary>
    private static string GetSeverityEmoji(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "🔴",
        FindingSeverity.Warning => "🟡",
        FindingSeverity.Suggestion => "💡",
        _ => "❓"
    };
}
