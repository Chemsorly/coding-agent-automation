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
        "agent:next" => "label-agent-next",
        "agent:in-progress" => "label-agent-progress",
        "agent:error" => "label-agent-error",
        "agent:needs-refinement" => "label-agent-refinement",
        "agent:epic" => "label-agent-epic",
        "agent:epic-approved" => "label-agent-epic-approved",
        "agent:epic-review" => "label-agent-epic-review",
        _ => ""
    };
}
