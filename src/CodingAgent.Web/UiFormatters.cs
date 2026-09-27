using System.Text.RegularExpressions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web;

public static class UiFormatters
{
    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    public static string TruncateUnicode(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    public static string FormatTimeAgo(DateTimeOffset timestamp)
    {
        var ago = DateTimeOffset.UtcNow - timestamp;
        if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s ago";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        return $"{(int)ago.TotalDays}d ago";
    }

    // Static compiled regex fields for StripMarkdown — reused across calls (avoids per-call
    // compilation cost) and carry an explicit timeout to satisfy S6444.
    // Pass order is load-bearing: bold (double-star) must run before italic (single-star),
    // and italic must run before the list-marker pass. Do not reorder.
    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromSeconds(1));
    private static readonly Regex ItalicRegex = new(@"\*(.+?)\*",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromSeconds(1));
    private static readonly Regex InlineCodeRegex = new(@"`(.+?)`",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromSeconds(1));
    // NOTE: back-to-back bold+italic spans (e.g. "**a***b*") leave a stray "*" — the bold pass
    // yields "a*b*" and the italic pass then correctly yields "a b". True adjacency
    // "**a***b*" is an exotic edge case and the output is acceptable plain text.
    private static readonly Regex BlockquoteRegex = new(@"^>\s*",
        RegexOptions.Compiled | RegexOptions.Multiline, matchTimeout: TimeSpan.FromSeconds(1));
    private static readonly Regex ListMarkerRegex = new(@"^[\-\*]\s+",
        RegexOptions.Compiled | RegexOptions.Multiline, matchTimeout: TimeSpan.FromSeconds(1));

    /// <summary>
    /// Strips common inline markdown syntax from a string, returning plain text.
    /// Handles: **bold**, *italic*, `code`, > blockquotes, - list items, * list items.
    /// URLs are preserved. Block-level heading stripping is done separately in GetBodyPreview.
    /// </summary>
    public static string StripMarkdown(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Strip **bold** and *italic* — order matters: bold (double) before italic (single)
        var result = BoldRegex.Replace(value, "$1");
        result = ItalicRegex.Replace(result, "$1");

        // Strip `inline code`
        result = InlineCodeRegex.Replace(result, "$1");

        // Strip leading blockquote marker (> at start of string, after optional whitespace)
        result = BlockquoteRegex.Replace(result, "");

        // Strip leading unordered list markers (- or * at start of line)
        // NOTE: The italic pass above must run before this one so that "*word* rest" is
        // handled correctly (italic stripped first, then no list marker remains).
        result = ListMarkerRegex.Replace(result, "");

        return result;
    }

    public static string GetLabelClass(string label) => label switch
    {
        AgentLabels.Next => "label-agent-next",
        AgentLabels.InProgress => "label-agent-progress",
        AgentLabels.Error => "label-agent-error",
        AgentLabels.NeedsRefinement => "label-agent-refinement",
        AgentLabels.Epic => "label-agent-epic",
        AgentLabels.EpicApproved => "label-agent-epic-approved",
        AgentLabels.EpicReview => "label-agent-epic-review",
        _ => ""
    };

    public static string FormatRunType(PipelineRunType runType) => runType switch
    {
        PipelineRunType.Review => "PR Review",
        PipelineRunType.DecompositionAnalysis => "Decomposition (Analysis)",
        PipelineRunType.Decomposition => "Decomposition",
        PipelineRunType.Consolidation => "Consolidation",
        _ => "Implementation"
    };
}
