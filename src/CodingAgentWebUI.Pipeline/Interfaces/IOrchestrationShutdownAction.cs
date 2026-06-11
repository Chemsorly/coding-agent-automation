namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for orchestration shutdown operations (agent run cancellation, label swaps).
/// Enables testability of shutdown logic without coupling to concrete services.
/// </summary>
public interface IOrchestrationShutdownAction
{
    Task CancelActiveAgentRunsAsync();
}
