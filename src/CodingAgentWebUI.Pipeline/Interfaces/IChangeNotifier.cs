namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface exposing only the state-change notification from
/// <see cref="Services.PipelineOrchestrationService"/>. Consumers that need
/// to signal UI re-renders depend on this interface rather than the full orchestration service.
/// </summary>
public interface IChangeNotifier
{
    /// <summary>Notifies subscribers of a state change (triggers UI re-render).</summary>
    void NotifyChange();
}
