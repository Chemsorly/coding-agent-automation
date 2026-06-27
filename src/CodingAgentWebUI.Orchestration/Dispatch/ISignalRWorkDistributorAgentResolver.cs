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
    /// The selected agent is atomically reserved (set to Busy).
    /// </summary>
    /// <param name="agentSelector">Sorted comma-joined agent labels (e.g., "dotnet,kiro").</param>
    string? ResolveConnectionId(string agentSelector);

    /// <summary>
    /// Reverts the last resolved agent back to Idle status.
    /// Call this when SignalR push fails after reservation to prevent the agent
    /// from being permanently stuck in Busy state.
    /// </summary>
    void ReleaseLastResolvedAgent();
}
