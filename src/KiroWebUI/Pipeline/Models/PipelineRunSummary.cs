namespace KiroWebUI.Pipeline.Models;

public sealed class PipelineRunSummary
{
    public required string RunId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueTitle { get; init; }
    public required PipelineStep FinalStep { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int RetryCount { get; init; }
    public string? PullRequestUrl { get; init; }
}
