using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.Registry;

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
    /// <para>
    /// <b>Label rule (rule 1):</b> every call stores a defensive copy of
    /// <c>message.Labels</c> (<c>message.Labels?.ToArray() ?? Array.Empty&lt;string&gt;()</c>).
    /// Re-registration replaces the previous label set so that <see cref="GetAgentsByLabel"/>
    /// always routes by the labels the agent currently reports.
    /// </para>
    /// <para>
    /// <b>Status rule (rule 2):</b> the resulting status is <see cref="AgentStatus.Busy"/> when
    /// the existing entry's <c>ActiveJobId</c> is non-null, and <see cref="AgentStatus.Idle"/>
    /// otherwise — regardless of the previous status.  When Busy, an existing <c>BusySince</c>
    /// is kept or set to now; when Idle, <c>BusySince</c> is cleared.  <c>DisconnectedAt</c>
    /// is always cleared on re-registration.
    /// </para>
    /// </summary>
    /// <param name="message">Registration message from the connecting agent.</param>
    /// <param name="connectionId">The new SignalR connection ID.</param>
    /// <param name="preserveExistingConnectionId">
    /// When <c>true</c>, the previous connection ID is kept in <c>_connectionIndex</c> alongside
    /// the new one. Use this for mid-run kiro-cli sub-process reconnects where the pipeline is
    /// still active on the old connection and must not lose its authorization context.
    /// Defaults to <c>false</c> (old connection evicted — normal re-registration behaviour).
    /// </param>
    AgentEntry Register(AgentRegistrationMessage message, string connectionId, bool preserveExistingConnectionId = false);

    /// <summary>
    /// Removes an agent from the registry entirely.
    /// </summary>
    bool Deregister(AgentId agentId);

    /// <summary>
    /// Looks up an agent by its unique agent identifier.
    /// <para>
    /// Reads from Redis to guarantee cross-replica visibility and reflect deregistrations
    /// or TTL expirations. Prefer <see cref="GetByAgentIdAsync"/> from async code paths.
    /// </para>
    /// </summary>
    AgentEntry? GetByAgentId(AgentId agentId);

    /// <summary>
    /// Looks up an agent by its unique agent identifier, reading fresh data from Redis.
    /// </summary>
    Task<AgentEntry?> GetByAgentIdAsync(AgentId agentId, CancellationToken ct = default);

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
    /// <para>
    /// Reads from Redis to ensure cross-replica visibility. Use
    /// <see cref="GetIdleAgentsAsync"/> for a pipelined batch variant that is more efficient
    /// when called frequently on the dispatch hot path.
    /// </para>
    /// </summary>
    IReadOnlyList<AgentEntry> GetIdleAgents();

    /// <summary>
    /// Returns all agents currently in <see cref="AgentStatus.Idle"/> status.
    /// Issues all HGETALL commands in a single pipelined batch, giving O(1) round-trips
    /// regardless of agent count.
    /// </summary>
    Task<IReadOnlyList<AgentEntry>> GetIdleAgentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all registered agents regardless of status.
    /// <para>
    /// Reads from Redis to ensure cross-replica visibility. Use
    /// <see cref="GetAllAgentsAsync"/> for a pipelined batch variant that is more efficient
    /// when called frequently.
    /// </para>
    /// </summary>
    IReadOnlyList<AgentEntry> GetAllAgents();

    /// <summary>
    /// Returns all registered agents regardless of status.
    /// Issues all HGETALL commands in a single pipelined batch, giving O(1) round-trips
    /// regardless of agent count.
    /// </summary>
    Task<IReadOnlyList<AgentEntry>> GetAllAgentsAsync(CancellationToken ct = default);

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
    /// <para>
    /// <b>Known fields (rule 3):</b> <c>activeJobId</c>, <c>activeChatSessionId</c>,
    /// <c>orphanRestoredAt</c>, <c>lastJobCompletedAt</c>, <c>disabled</c>
    /// (ordinal, case-sensitive).
    /// </para>
    /// <para>
    /// <b>Empty values (rule 4):</b> a <c>null</c> or empty <paramref name="value"/> clears the
    /// field — <c>null</c> for the two ID fields and the two timestamp fields, <c>false</c> for
    /// <c>disabled</c>.
    /// </para>
    /// <para>
    /// <b>Malformed values (rule 5):</b> a non-empty timestamp value that fails
    /// <c>DateTimeOffset.TryParse</c> (RoundtripKind), or a non-empty <c>disabled</c> value that
    /// fails <c>bool.TryParse</c>, is logged as a warning and ignored — nothing is stored, nothing
    /// is thrown, and the previous value is preserved.
    /// </para>
    /// <para>
    /// <b>Unknown fields (rule 6):</b> an unrecognised <paramref name="field"/> name is logged as
    /// a warning and ignored; nothing is written.
    /// </para>
    /// </summary>
    Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value);

    /// <summary>
    /// Synchronously updates the local in-memory snapshot for the specified agent.
    /// Does <b>not</b> write to Redis. Use when the caller needs <see cref="GetByConnectionId"/>
    /// to reflect the new field value immediately, before a fire-and-forget Redis write completes.
    /// <para>
    /// Only meaningful for <see cref="DistributedAgentRegistryService"/>; all other implementations
    /// are no-ops because they either return live object references (in-memory) or hold read-only
    /// snapshots (API replica).
    /// </para>
    /// </summary>
    void SetLocalSnapshotField(AgentId agentId, string field, string? value);
}
