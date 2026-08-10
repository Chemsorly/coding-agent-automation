namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for orchestration shutdown operations — releases active agent runs for handoff
/// to the successor pod during a rolling update.
/// Enables testability of shutdown logic without coupling to concrete services.
/// </summary>
public interface IOrchestrationShutdownAction
{
    Task ReleaseActiveAgentRunsAsync();
}
