using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for querying active pipeline run state for UI display.
/// In Legacy/single-instance mode: delegates to <see cref="IOrchestratorRunService"/> in-memory state.
/// In DB mode: queries WorkItems table directly, enabling non-leader replicas to display
/// current run state from Postgres without cross-replica in-memory sync.
/// </summary>
public interface IActiveRunQueryService
{
    /// <summary>
    /// Returns summaries of all currently active (non-terminal) pipeline runs for dashboard display.
    /// </summary>
    Task<IReadOnlyList<ActiveRunSummary>> GetActiveRunsAsync(CancellationToken ct = default);
}

/// <summary>
/// Lightweight summary of an active run, projected for dashboard display.
/// Avoids exposing full PipelineRun internals across assembly boundaries.
/// </summary>
public sealed record ActiveRunSummary
{
    public required string RunId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueTitle { get; init; }
    public required PipelineRunType RunType { get; init; }
    public required string? AgentId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string? ProjectName { get; init; }

    /// <summary>
    /// Current pipeline step. In DB mode, maps from WorkItemStatus (Dispatched→Running step);
    /// in Legacy mode, reflects the actual PipelineStep from in-memory state.
    /// </summary>
    public required PipelineStep CurrentStep { get; init; }
}
