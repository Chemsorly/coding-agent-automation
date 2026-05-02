namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Structured assessment written by the agent after analysis, used by the confidence gate
/// to decide whether to proceed to implementation, abort, or close the issue.
/// </summary>
public sealed class AnalysisAssessment
{
    // NOTE: [ARC-08a] `required` is not enforced by System.Text.Json deserialization — Recommendation can be null at runtime despite non-nullable type. Consider changing to `string?` or adding post-deserialization validation.
    public required string Recommendation { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> Concerns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingIssues { get; init; } = Array.Empty<string>();
    public string? PlannedApproach { get; init; }
    public string? EstimatedComplexity { get; init; }
}
