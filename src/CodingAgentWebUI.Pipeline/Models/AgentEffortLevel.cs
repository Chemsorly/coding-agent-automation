using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Reasoning effort levels supported by Kiro CLI.
/// Controls how much reasoning the model applies — lower is faster/cheaper, higher is deeper.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentEffortLevel>))]
public enum AgentEffortLevel
{
    /// <summary>Use the CLI default (no --effort flag passed).</summary>
    Auto,

    /// <summary>Fast, concise responses. Good for simple questions and quick lookups.</summary>
    Low,

    /// <summary>Balanced reasoning. Suitable for most development tasks.</summary>
    Medium,

    /// <summary>Thorough analysis. Better for complex refactoring and architecture decisions.</summary>
    High,

    /// <summary>Extended reasoning. Useful for multi-file changes and nuanced problems.</summary>
    XHigh,

    /// <summary>Maximum depth. Best for difficult debugging, security analysis, and intricate logic.</summary>
    Max
}

/// <summary>
/// Extension methods for <see cref="AgentEffortLevel"/>.
/// </summary>
public static class AgentEffortLevelExtensions
{
    /// <summary>
    /// Converts the enum value to the CLI flag value (lowercase string).
    /// Returns null for <see cref="AgentEffortLevel.Auto"/>.
    /// </summary>
    public static string? ToCliValue(this AgentEffortLevel level) => level switch
    {
        AgentEffortLevel.Auto => null,
        AgentEffortLevel.Low => "low",
        AgentEffortLevel.Medium => "medium",
        AgentEffortLevel.High => "high",
        AgentEffortLevel.XHigh => "xhigh",
        AgentEffortLevel.Max => "max",
        _ => null
    };

    /// <summary>
    /// Parses a string value (from settings) to an <see cref="AgentEffortLevel"/>.
    /// Returns <see cref="AgentEffortLevel.Auto"/> for null/empty/unrecognized values.
    /// </summary>
    public static AgentEffortLevel ParseEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return AgentEffortLevel.Auto;

        return value.ToLowerInvariant() switch
        {
            "low" => AgentEffortLevel.Low,
            "medium" => AgentEffortLevel.Medium,
            "high" => AgentEffortLevel.High,
            "xhigh" => AgentEffortLevel.XHigh,
            "max" => AgentEffortLevel.Max,
            _ => AgentEffortLevel.Auto
        };
    }
}
