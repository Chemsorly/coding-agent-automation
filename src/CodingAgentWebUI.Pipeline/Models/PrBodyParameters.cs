namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Parameters for generating a pull request body via <see cref="Services.PipelineFormatting.GeneratePrBody"/>.
/// </summary>
public sealed record PrBodyParameters
{
    public required string IssueReference { get; init; }
    public required int TestsPassed { get; init; }
    public required int TestsFailed { get; init; }
    public required int TestsSkipped { get; init; }
    public double? CoveragePercent { get; init; }
    public required IReadOnlyList<FileChangeSummary> FileChanges { get; init; }
    public required string IssueTitle { get; init; }
    public bool IsDraft { get; init; }
    public IReadOnlyList<IssueComment>? Comments { get; init; }
    public IReadOnlyList<string>? BlacklistedFilesDetected { get; init; }
    public string? ModelName { get; init; }
    public CodeReviewSummary? CodeReviewSummary { get; init; }
    public string? CloseReference { get; init; }
    public AcceptanceCriteriaReport? ComplianceReport { get; init; }
}
