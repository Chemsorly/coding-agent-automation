namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface for state-change notification. Consumers that need to signal UI re-renders
/// or subscribe to state changes depend on this interface rather than the full orchestration service.
/// Implemented by <see cref="Services.PipelineRunLifecycleService"/>.
/// </summary>
public interface IChangeNotifier
{
    /// <summary>Fired after each state transition for UI binding.</summary>
    event Action? OnChange;

    /// <summary>Notifies subscribers of a state change (triggers UI re-render).</summary>
    void NotifyChange();
}
