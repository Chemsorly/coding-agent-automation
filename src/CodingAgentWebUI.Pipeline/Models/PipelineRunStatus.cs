namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Aggregate status of all CI/CD pipeline runs for a given branch/commit.
/// </summary>
public sealed class PipelineRunStatus
{
    public required PipelineRunState State { get; init; }
    public required IReadOnlyList<PipelineJobResult> Jobs { get; init; }
    public string? Url { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? CommitSha { get; init; }
}
