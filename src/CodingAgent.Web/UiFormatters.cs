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

    public static string FormatRunType(PipelineRunType runType) => runType switch
    {
        PipelineRunType.Review => "PR Review",
        PipelineRunType.DecompositionAnalysis => "Decomposition (Analysis)",
        PipelineRunType.Decomposition => "Decomposition",
        PipelineRunType.Consolidation => "Consolidation",
        _ => "Implementation"
    };

    public static string FormatDuration(DateTime startedAt, DateTime? completedAt)
    {
        if (completedAt is null) return "—";
        var duration = completedAt.Value - startedAt;
        return duration.ToString(@"hh\:mm\:ss");
    }

    public static string FormatTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        var ago = DateTime.UtcNow - utc;
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 7) return $"{(int)ago.TotalDays}d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
