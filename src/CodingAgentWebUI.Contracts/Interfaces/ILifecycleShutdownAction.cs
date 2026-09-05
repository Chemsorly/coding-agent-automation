namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for pipeline lifecycle shutdown operations.
/// Enables testability of shutdown logic without coupling to concrete services.
/// </summary>
public interface ILifecycleShutdownAction
{
    bool IsRunning { get; }
    Task CancelPipelineAsync();
}
