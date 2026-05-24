using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Formats individual inline comment bodies with severity emoji, message, and agent attribution.
/// Handles consolidation of multiple findings at the same file:line.
/// </summary>
internal static class InlineCommentFormatter
{
    private const int MaxCommentLength = 65536;
    private const string Separator = "\n\n---\n\n";

    /// <summary>
    /// Formats a single finding as an inline comment body.
    /// Format: "{emoji} **{SEVERITY}**: {message}\n— *{AgentName}*"
    /// </summary>
    public static string FormatSingle(StructuredFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var prefix = GetSeverityPrefix(finding.Severity);
        return $"{prefix}: {finding.Message}\n— *{finding.AgentName}*";
    }

    /// <summary>
    /// Consolidates multiple findings at the same file:line into a single comment body.
    /// Findings separated by horizontal rule (---), ordered by severity (Critical first).
    /// Total length capped at 65536 characters.
    /// </summary>
    public static string FormatConsolidated(IReadOnlyList<StructuredFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        if (findings.Count == 0)
            return string.Empty;

        if (findings.Count == 1)
            return FormatSingle(findings[0]);

        // Order by severity descending (Critical=2 first, Suggestion=0 last)
        var ordered = findings.OrderByDescending(f => f.Severity).ToList();

        var parts = new List<string>(ordered.Count);
        var totalLength = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var formatted = FormatSingle(ordered[i]);
            var additionalLength = i == 0
                ? formatted.Length
                : Separator.Length + formatted.Length;

            if (totalLength + additionalLength > MaxCommentLength)
                break;

            parts.Add(formatted);
            totalLength += additionalLength;
        }

        return string.Join(Separator, parts);
    }

    private static string GetSeverityPrefix(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "🔴 **CRITICAL**",
        FindingSeverity.Warning => "🟡 **WARNING**",
        FindingSeverity.Suggestion => "💡 **SUGGESTION**",
        _ => "💡 **SUGGESTION**"
    };
}
