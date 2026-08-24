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
    /// Returns all agents whose labels contain <c>{labelKey}={labelValue}</c>.
    /// Used by <c>ChatJobDispatcher</c> to find a newly-connected chat pod.
    /// </summary>
    IReadOnlyList<AgentEntry> GetAgentsByLabel(string labelKey, string labelValue);

    /// <summary>
    /// Updates a single field on the agent's registry entry.
    /// Callers that previously mutated <see cref="AgentEntry"/> properties directly must use this
    /// instead — under <c>DistributedAgentRegistryService</c>, <see cref="GetByAgentId"/> returns
    /// a deserialized snapshot and direct mutations are silently lost.
    /// </summary>
    Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value);
}
