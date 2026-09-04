using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Confidence gate recommendation from the analysis assessment.
/// Determines whether the pipeline proceeds to implementation, aborts, or closes the issue.
/// </summary>
public enum AnalysisGateResult
{
    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("not_ready")]
    NotReady,

    [JsonStringEnumMemberName("wont_do")]
    WontDo
}
