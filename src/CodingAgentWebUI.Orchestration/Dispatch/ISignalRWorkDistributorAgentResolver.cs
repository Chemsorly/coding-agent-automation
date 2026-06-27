namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Resolves the SignalR connection ID for an agent matching the given selector labels.
/// Implemented by the WebUI project using <see cref="Registry.AgentRegistryService"/>
/// and <see cref="JobDispatcherService"/> to select and reserve an idle, label-compatible agent.
/// </summary>
public interface ISignalRWorkDistributorAgentResolver
{
    /// <summary>
    /// Returns the SignalR connection ID of a suitable agent for the given selector.
    /// Returns <c>null</c> if no compatible idle agent is available.
    /// </summary>
    /// <param name="agentSelector">Sorted comma-joined agent labels (e.g., "dotnet,kiro").</param>
    string? ResolveConnectionId(string agentSelector);
}
