using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.CodeReview.Models;

/// <summary>
/// Severity level for structured code review findings.
/// Higher numeric value = more severe. Natural >= comparison for threshold filtering:
/// a finding is eligible when its value >= the configured threshold value.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindingSeverity
{
    /// <summary>Low-priority improvement suggestion.</summary>
    Suggestion = 0,

    /// <summary>Potential issue that should be addressed.</summary>
    Warning = 1,

    /// <summary>Serious issue that must be fixed before merge.</summary>
    Critical = 2
}
