namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>Humanizes raw pipeline phase keys for display.</summary>
public static class PhaseNameFormatter
{
    /// <summary>
    /// Converts a raw phase key (e.g. "review_Correctness") into a human-readable label
    /// (e.g. "Review: Correctness").
    /// </summary>
    public static string HumanizePhase(string phase) => phase switch
    {
        null or "" => "(unknown)",
        var p when p.StartsWith("review_") => $"Review: {p[7..]}",
        var p when p.StartsWith("follow_up_") => $"Follow-up: {p[10..]}",
        _ => char.ToUpper(phase[0]) + phase[1..].Replace('_', ' ')
    };
}
