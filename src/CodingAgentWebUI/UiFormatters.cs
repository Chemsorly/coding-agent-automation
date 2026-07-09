using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI;

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
        return $"{(int)ago.TotalHours}h ago";
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
}
