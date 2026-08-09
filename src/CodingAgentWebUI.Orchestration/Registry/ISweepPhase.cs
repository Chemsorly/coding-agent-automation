using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Represents a single phase in the per-agent heartbeat sweep.
/// Each phase inspects an agent and optionally acts on it.
/// </summary>
public interface ISweepPhase
{
    /// <summary>
    /// Executes this sweep phase for the specified agent.
    /// Returns <c>true</c> if this phase consumed the agent and further phases in the
    /// current list should be skipped (equivalent to <c>continue</c> in the original loop).
    /// Returns <c>false</c> if subsequent phases should still run.
    /// </summary>
    Task<bool> ExecuteAsync(AgentEntry agent, DateTimeOffset now, PipelineConfiguration config, CancellationToken ct);
}
