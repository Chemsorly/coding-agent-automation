using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Structured compliance report produced by the AcceptanceCriteria agent.
/// Parsed from .agent/acceptance-criteria.json after agent execution.
/// </summary>
public sealed record AcceptanceCriteriaReport
{
    public required IReadOnlyList<CriterionResult> Criteria { get; init; }
    public required string Summary { get; init; }
}

public sealed record CriterionResult
{
    public required string Criterion { get; init; }
    public required CriterionStatus Status { get; init; }
    public string? Evidence { get; init; }
    public string? Reasoning { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<CriterionStatus>))]
public enum CriterionStatus
{
    Compliant,
    [JsonStringEnumMemberName("non_compliant")]
    NonCompliant,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable
}
