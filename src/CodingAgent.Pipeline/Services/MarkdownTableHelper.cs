using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Shared markdown table formatting utilities for pipeline comment renderers.
/// Extracted to eliminate duplication across PipelineFormatting, ReviewFindingsFormatter,
/// and PostDecompositionSummaryStep.
/// </summary>
internal static class MarkdownTableHelper
{
    /// <summary>
    /// Escapes pipe characters and replaces newlines to keep markdown table cells intact.
    /// </summary>
    internal static string SanitizeTableCell(string text)
        => text.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");

    /// <summary>
    /// Appends a fully-formatted acceptance-criteria compliance table to <paramref name="sb"/>,
    /// including the heading line. The <paramref name="heading"/> (e.g. "## Acceptance Criteria Compliance")
    /// is written as-is as the first line of output.
    /// </summary>
    internal static void AppendAcceptanceCriteriaTable(
        StringBuilder sb, AcceptanceCriteriaReport report, string heading)
    {
        sb.AppendLine(heading);
        sb.AppendLine();
        sb.AppendLine("| Status | Criterion | Notes |");
        sb.AppendLine("|--------|-----------|-------|");
        foreach (var c in report.Criteria)
        {
            var icon = c.Status switch
            {
                CriterionStatus.Compliant => "✅",
                CriterionStatus.NonCompliant => "❌",
                CriterionStatus.NotApplicable => "⚠️",
                _ => "❓"
            };
            var notes = SanitizeTableCell(c.Evidence ?? c.Reasoning ?? "");
            sb.AppendLine($"| {icon} | {SanitizeTableCell(c.Criterion)} | {notes} |");
        }
        sb.AppendLine();
        sb.AppendLine($"*{report.Summary}*");
        sb.AppendLine();
    }
}
