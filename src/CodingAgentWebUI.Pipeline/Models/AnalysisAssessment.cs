namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Structured assessment written by the agent after analysis, used by the confidence gate
/// to decide whether to proceed to implementation, abort, or close the issue.
/// </summary>
public sealed class AnalysisAssessment
{
    // NOTE: [ARC-08a] STJ throws when the field is entirely missing (enforced at deserialization).
    // However, an explicit null value (e.g., {"recommendation": null}) bypasses this check and
    // assigns null to the non-nullable string. ReadAssessmentAsync validates post-deserialization.
    public required string Recommendation { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> Concerns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingIssues { get; init; } = Array.Empty<string>();
    public string? PlannedApproach { get; init; }
    public string? EstimatedComplexity { get; init; }
}
