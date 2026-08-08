using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// In-memory registry of connected agents. Provides agent lookup, status transitions,
/// and idle agent selection for dispatch services.
/// <para>
/// Extracted from the concrete <see cref="AgentRegistryService"/> to enable testability
/// of consumers without requiring a full registry implementation.
/// </para>
/// </summary>
public interface IAgentRegistryService
{
    /// <summary>
    /// Registers an agent or updates an existing entry on reconnection.
    /// </summary>
    AgentEntry Register(AgentRegistrationMessage message, string connectionId);

    /// <summary>
    /// Removes an agent from the registry entirely.
    /// </summary>
    bool Deregister(AgentId agentId);

    /// <summary>
    /// Looks up an agent by its unique agent identifier.
    /// </summary>
    AgentEntry? GetByAgentId(AgentId agentId);

    /// <summary>
    /// Looks up an agent by its current SignalR connection ID.
    /// </summary>
    AgentEntry? GetByConnectionId(string connectionId);

    /// <summary>
    /// Updates the heartbeat timestamp for the specified agent.
    /// </summary>
    void UpdateHeartbeat(AgentId agentId, DateTimeOffset timestamp);

    /// <summary>
    /// Transitions an agent to a new status.
    /// </summary>
    void TransitionStatus(AgentId agentId, AgentStatus newStatus);

    /// <summary>
    /// Returns all agents currently in <see cref="AgentStatus.Idle"/> status.
    /// </summary>
    IReadOnlyList<AgentEntry> GetIdleAgents();

    /// <summary>
    /// Returns all registered agents regardless of status.
    /// </summary>
    IReadOnlyList<AgentEntry> GetAllAgents();

    /// <summary>
    /// Returns the count of agents currently in <see cref="AgentStatus.Busy"/> status.
    /// </summary>
    int GetBusyAgentCount();

    /// <summary>
    /// Clears an agent's active job state and transitions it to <see cref="AgentStatus.Idle"/>.
    /// Null-safe: does nothing if <paramref name="agentId"/> is null or empty, or if the agent is not found.
    /// Acquires the agent's SyncRoot lock when clearing fields.
    /// </summary>
    /// <remarks>
    /// Performs a two-phase operation: field clears under SyncRoot lock, then
    /// <see cref="TransitionStatus"/> (which acquires SyncRoot internally).
    /// Do NOT call <see cref="TransitionStatus"/> while holding SyncRoot — that would deadlock.
    /// </remarks>
    void ClearAgentState(string? agentId);
}
