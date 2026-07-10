namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Result of a successful agent resolution. Carries both the connection ID (for SignalR push)
/// and the agent ID (for DB tracking and failure rollback) as an atomic unit.
/// This eliminates the thread-safety issue of storing "last resolved" in a shared field.
/// </summary>
public sealed record AgentResolveResult(string ConnectionId, string AgentId);

/// <summary>
/// Resolves the SignalR connection ID for an agent matching the given selector labels.
/// Implemented by the WebUI project using <see cref="Registry.AgentRegistryService"/>
/// and <see cref="JobDispatcherService"/> to select and reserve an idle, label-compatible agent.
/// </summary>
public interface ISignalRWorkDistributorAgentResolver
{
    /// <summary>
    /// Resolves a suitable agent and returns both connection ID and agent ID atomically.
    /// Returns <c>null</c> if no compatible idle agent is available.
    /// The selected agent is atomically reserved (set to Busy).
    /// Thread-safe: no shared mutable state — result is returned to the caller.
    /// </summary>
    /// <param name="agentSelector">Sorted comma-joined agent labels (e.g., "dotnet,kiro").</param>
    AgentResolveResult? ResolveAgent(string agentSelector);

    /// <summary>
    /// Reverts a specific agent back to Idle status.
    /// Call this when SignalR push fails after reservation to prevent the agent
    /// from being permanently stuck in Busy state.
    /// Thread-safe: operates on the explicit agent ID, not shared state.
    /// </summary>
    /// <param name="agentId">The agent ID to release (from <see cref="AgentResolveResult.AgentId"/>).</param>
    void ReleaseAgent(string agentId);

    /// <summary>
    /// Sets the active job ID on the agent entry after successful dispatch.
    /// Must be called after SignalR delivery succeeds so HeartbeatMonitor and the
    /// monitoring UI can correlate the agent with its active run.
    /// </summary>
    /// <param name="agentId">The agent ID (from <see cref="AgentResolveResult.AgentId"/>).</param>
    /// <param name="jobId">The run/work-item ID assigned to this agent.</param>
    void AssignJob(string agentId, string jobId);
}
