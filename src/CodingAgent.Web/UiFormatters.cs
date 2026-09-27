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

    /// <summary>
    /// Strips common inline markdown syntax from a string, returning plain text.
    /// Handles: **bold**, *italic*, `code`, > blockquotes, - list items, * list items.
    /// URLs are preserved. Block-level heading stripping is done separately in GetBodyPreview.
    /// </summary>
    // TODO [WARNING]: These Regex.Replace calls compile a new Regex object on every invocation.
    // Because StripMarkdown is called once per issue row in the drawer list and Blazor Server
    // re-renders on each state change, this causes repeated regex compilation on large lists.
    // Promote these patterns to static readonly Regex fields (or use [GeneratedRegex] attributes)
    // so the compiled automaton is reused across calls.
    public static string StripMarkdown(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Strip **bold** and *italic* — order matters: bold (double) before italic (single)
        // Use a non-greedy match to avoid consuming across multiple markup spans.
        // TODO [WARNING]: Back-to-back bold+italic spans (e.g. "**a***b*") leave a stray "*"
        // after the bold pass consumes "**a**", yielding "*b*" → italic pass yields "b", but
        // input "**a***b*" (double-star then single-star immediately adjacent) yields "a**b*"
        // after bold pass → "*b" after italic pass → one stray "*" remains in output.
        // This narrow edge case only affects exotic contiguous bold+italic markup.
        var result = Regex.Replace(value, @"\*\*(.+?)\*\*", "$1");
        result = Regex.Replace(result, @"\*(.+?)\*", "$1");

        // Strip `inline code`
        result = Regex.Replace(result, @"`(.+?)`", "$1");

        // Strip leading blockquote marker (> at start of string, after optional whitespace)
        result = Regex.Replace(result, @"^>\s*", "", RegexOptions.Multiline);

        // Strip leading unordered list markers (- or * at start of line, after optional whitespace)
        // NOTE: Pass order here is load-bearing. The italic pass above must run before this one:
        // a line "*word* rest" is correctly handled (italic stripped first, then no list marker
        // remains). Reversing the order would strip "*word*" as a list marker before italic
        // processing runs, also producing "word* rest" (incorrect). Do not reorder these passes.
        result = Regex.Replace(result, @"^[\-\*]\s+", "", RegexOptions.Multiline);

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
