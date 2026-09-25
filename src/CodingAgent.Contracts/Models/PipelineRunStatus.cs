namespace CodingAgent.Pipeline.Models;

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

    /// <summary>
    /// When <c>true</c>, the CI-never-started exhaustion path produced this status.
    /// The quality gate retry loop must not invoke the LLM fix agent for infrastructure failures —
    /// there is no code problem to fix; the CI pipeline simply never triggered.
    /// </summary>
    public bool IsInfrastructureFailure { get; init; }
}
