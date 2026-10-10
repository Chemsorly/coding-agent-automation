using System.Collections.Concurrent;
using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Registry;

/// <summary>
/// In-memory registry of connected agents. Tracks agent status, heartbeats,
/// and active job assignments. Registered as a singleton in DI.
/// </summary>
public sealed class AgentRegistryService : IAgentRegistryService
{
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
    private readonly ConcurrentDictionary<string, AgentEntry> _connectionIndex = new();
    private readonly ILogger _logger;

    public AgentRegistryService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Registers an agent or updates an existing entry on reconnection.
    /// Re-registration with the same <paramref name="message"/>.<c>AgentId</c> updates
    /// the <c>ConnectionId</c> and resets status to <see cref="AgentStatus.Idle"/> if
    /// the agent was <see cref="AgentStatus.Disconnected"/>.
    /// </summary>
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId, bool preserveExistingConnectionId = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connectionId);

        var now = DateTimeOffset.UtcNow;

        // Note: Any AgentId constructed via new AgentId(value) or the implicit string operator already
        // rejects null and empty strings (ArgumentException.ThrowIfNullOrEmpty in both paths). A malformed
        // MessagePack payload with a nil/empty token is rejected by AgentIdFormatter.Deserialize before
        // reaching this method. The only unguarded path is default(AgentId) (C# struct zero-init,
        // Value = null), which bypasses the constructor entirely. If callers can pass an uninitialized
        // struct, consider adding ArgumentException.ThrowIfNullOrEmpty(message.AgentId.Value, nameof(message))
        // above this line for a descriptive error instead of a NullReferenceException from AddOrUpdate.
        var entry = _agents.AddOrUpdate(
            message.AgentId.Value,
            // Add factory — brand new registration
            _ =>
            {
                _logger.Information(
                    "Agent {AgentId} registered (labels=[{Labels}], connection={ConnectionId})",
                    message.AgentId, LogSanitizer.SanitizeForLog(string.Join(", ", message.Labels)), connectionId);

                return new AgentEntry
                {
                    AgentId = message.AgentId.Value,
                    ConnectionId = connectionId,
                    Hostname = message.Hostname,
                    // Defensive copy: break the aliasing between AgentEntry.Labels and
                    // message.Labels. MessagePack deserializes IReadOnlyList<string> as a
                    // mutable List<string> at runtime. Storing the reference directly means
                    // external callers holding the same message object could mutate the list
                    // while OnDisconnectedAsync or GetAgentsByLabel iterates it, causing
                    // InvalidOperationException. ToArray() produces an immutable fixed-length
                    // copy that is safe for lock-free concurrent reads.
                    Labels = message.Labels?.ToArray() ?? Array.Empty<string>(),
                    Status = AgentStatus.Idle,
                    RegisteredAt = now,
                    LastHeartbeatAt = now
                };
            },
            // Update factory — re-registration (reconnection)
            (_, existing) =>
            {
                lock (existing.SyncRoot)
                {
                    // Remove old connectionId from index before updating — unless the caller has
                    // asked us to keep it (mid-run kiro-cli sub-process reconnect: the pipeline is
                    // still active on the old connection and must not lose its auth context).
                    if (!preserveExistingConnectionId)
                        _connectionIndex.TryRemove(existing.ConnectionId, out AgentEntry? _);

                    // Rule 1: replace labels on every re-registration (defensive copy so the
                    // stored array is never aliased to the caller's mutable list). Reference store
                    // is atomic on 64-bit .NET so lock-free readers of Labels always see either
                    // the old or the new array — never a torn reference.
                    existing.Labels = message.Labels?.ToArray() ?? Array.Empty<string>();

                    existing.ConnectionId = connectionId;
                    existing.LastHeartbeatAt = now;
                    existing.DisconnectedAt = null;

                    // Rule 2: status is Busy when ActiveJobId is set, Idle otherwise —
                    // regardless of the previous status (aligns with DistributedAgentRegistryService).
                    var previousStatus = existing.Status;
                    if (existing.ActiveJobId is not null)
                    {
                        existing.Status = AgentStatus.Busy;
                        existing.BusySince ??= DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        existing.Status = AgentStatus.Idle;
                        existing.BusySince = null;
                    }

                    _logger.Information(
                        "Agent {AgentId} re-registered after {PreviousStatus} (connection={ConnectionId}, activeJob={JobId})",
                        message.AgentId, previousStatus, connectionId, existing.ActiveJobId ?? "none");
                }

                return existing;
            });

        // Update connection index (add factory path + update factory path converge here)
        _connectionIndex[connectionId] = entry;

        return entry;
    }

    /// <summary>
    /// Removes an agent from the registry entirely.
    /// </summary>
    public bool Deregister(AgentId agentId)
    {
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) throughout this class.
        // ThrowIfNull on a struct field always reports parameter name "Value" (not "agentId") in exception
        // messages, making diagnostics misleading. ThrowIfNullOrEmpty also rejects empty strings.
        ArgumentNullException.ThrowIfNull(agentId.Value);

        if (_agents.TryRemove(agentId.Value, out var removed))
        {
            // TODO (WARNING, issue #2758): When Register was called with preserveExistingConnectionId=true
            // (mid-run kiro-cli reconnect), _connectionIndex retains both conn-A and conn-B.
            // Deregister only removes removed.ConnectionId (conn-B, the primary set by
            // existing.ConnectionId = connectionId at registration time); the stale conn-A key is
            // never removed. Functionally benign — GetByConnectionId("conn-A") returns null once
            // the entry is gone from _agents — but _connectionIndex retains the orphaned key
            // indefinitely. Consider removing the original connection ID during deregister or
            // cleaning it up in OnDisconnectedAsync when the stale connection closes.
            _connectionIndex.TryRemove(removed.ConnectionId, out AgentEntry? _);
            _logger.Information("Agent {AgentId} deregistered", agentId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Looks up an agent by its unique agent identifier.
    /// </summary>
    public AgentEntry? GetByAgentId(AgentId agentId)
    {
        // TODO: See Deregister — replace with ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)).
        ArgumentNullException.ThrowIfNull(agentId.Value);
        return _agents.TryGetValue(agentId.Value, out var entry) ? entry : null;
    }

    /// <inheritdoc />
    public Task<AgentEntry?> GetByAgentIdAsync(AgentId agentId, CancellationToken ct = default)
        => Task.FromResult(GetByAgentId(agentId));

    /// <summary>
    /// Looks up an agent by its current SignalR connection ID.
    /// Uses an O(1) reverse-lookup index maintained by Register/Deregister.
    /// </summary>
    public AgentEntry? GetByConnectionId(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        return _connectionIndex.TryGetValue(connectionId, out var entry) ? entry : null;
    }

    /// <summary>
    /// Updates the heartbeat timestamp for the specified agent.
    /// </summary>
    public void UpdateHeartbeat(AgentId agentId, DateTimeOffset timestamp)
    {
        // TODO: See Deregister — replace with ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)).
        ArgumentNullException.ThrowIfNull(agentId.Value);

        if (_agents.TryGetValue(agentId.Value, out var entry))
        {
            lock (entry.SyncRoot)
            {
                entry.LastHeartbeatAt = timestamp;
            }
        }
        else
        {
            _logger.Warning("Heartbeat received for unknown agent {AgentId}", agentId);
        }
    }

    /// <summary>
    /// Transitions an agent to a new status. Records <c>DisconnectedAt</c> when
    /// transitioning to <see cref="AgentStatus.Disconnected"/>.
    /// </summary>
    public void TransitionStatus(AgentId agentId, AgentStatus newStatus)
    {
        // TODO: See Deregister — replace with ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)).
        ArgumentNullException.ThrowIfNull(agentId.Value);

        if (_agents.TryGetValue(agentId.Value, out var entry))
        {
            lock (entry.SyncRoot)
            {
                var oldStatus = entry.Status;

                // Reject Disconnected → Busy: must go through Register for reconnection
                if (oldStatus == AgentStatus.Disconnected && newStatus == AgentStatus.Busy)
                {
                    _logger.Warning(
                        "Agent {AgentId} invalid transition {OldStatus} → {NewStatus} rejected (must re-register first)",
                        agentId, oldStatus, newStatus);
                    return;
                }

                entry.Status = newStatus;

                if (newStatus == AgentStatus.Busy)
                {
                    entry.BusySince = DateTimeOffset.UtcNow;
                }
                else
                {
                    entry.BusySince = null;
                }

                if (newStatus == AgentStatus.Disconnected)
                {
                    entry.DisconnectedAt = DateTimeOffset.UtcNow;
                }
                else if (newStatus == AgentStatus.Idle)
                {
                    entry.DisconnectedAt = null;
                }

                _logger.Information(
                    "Agent {AgentId} status transitioned {OldStatus} → {NewStatus}",
                    agentId, oldStatus, newStatus);
            }
        }
        else
        {
            _logger.Warning("Cannot transition status for unknown agent {AgentId}", agentId);
        }
    }

    /// <summary>
    /// Returns all agents currently in <see cref="AgentStatus.Idle"/> status.
    /// </summary>
    public IReadOnlyList<AgentEntry> GetIdleAgents()
    {
        return _agents.Values
            .Where(a => a.Status == AgentStatus.Idle)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEntry>> GetIdleAgentsAsync(CancellationToken ct = default)
        => Task.FromResult(GetIdleAgents());

    /// <summary>
    /// Returns all registered agents regardless of status.
    /// </summary>
    public IReadOnlyList<AgentEntry> GetAllAgents()
    {
        return _agents.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEntry>> GetAllAgentsAsync(CancellationToken ct = default)
        => Task.FromResult(GetAllAgents());

    /// <summary>
    /// Returns the count of agents currently in <see cref="AgentStatus.Busy"/> status.
    /// </summary>
    public int GetBusyAgentCount()
    {
        return _agents.Values.Count(a => a.Status == AgentStatus.Busy);
    }

    /// <summary>
    /// Returns all registered agents whose <see cref="AgentEntry.Labels"/> array contains
    /// <c>"{labelKey}={labelValue}"</c>. Matching is case-insensitive (OrdinalIgnoreCase).
    /// Used by <c>ChatJobDispatcher</c> to identify a newly-connected chat pod by its
    /// <c>chat-session-id</c> label.
    /// </summary>
    /// <param name="labelKey">Label key (e.g. <c>"chat-session-id"</c>).</param>
    /// <param name="labelValue">Label value (e.g. the dispatch GUID as a string).</param>
    /// <returns>Read-only list of matching agents; empty if none found.</returns>
    public IReadOnlyList<AgentEntry> GetAgentsByLabel(string labelKey, string labelValue)
    {
        var target = $"{labelKey}={labelValue}";
        return _agents.Values
            .Where(a => a.Labels?.Any(l => string.Equals(l, target, StringComparison.OrdinalIgnoreCase)) == true)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Clears all registered agents. Used by E2E tests for state isolation.
    /// </summary>
    internal void Reset()
    {
        _agents.Clear();
        _connectionIndex.Clear();
    }

    /// <inheritdoc />
    public Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        if (!_agents.TryGetValue(agentId.Value, out var entry))
        {
            _logger.Warning("UpdateAgentFieldAsync: agent {AgentId} not found (field={Field})", agentId, field);
            return Task.CompletedTask;
        }

        lock (entry.SyncRoot)
        {
            if (!AgentEntryFieldApplier.IsValid(field, value))
            {
                _logger.Warning(
                    "UpdateAgentFieldAsync: malformed value '{Value}' for field '{Field}' on agent {AgentId} — ignoring",
                    value, field, agentId);
                return Task.CompletedTask;
            }

            if (field is not (AgentFieldNames.ActiveJobId
                or AgentFieldNames.ActiveChatSessionId
                or AgentFieldNames.OrphanRestoredAt
                or AgentFieldNames.LastJobCompletedAt
                or AgentFieldNames.Disabled))
            {
                _logger.Warning("UpdateAgentFieldAsync: unknown field '{Field}' for agent {AgentId}", field, agentId);
                return Task.CompletedTask;
            }

            // TODO (WARNING): Maintenance trap — AgentEntry is a mutable record and the in-memory path
            // cannot use the returned record directly (callers hold a reference to the live entry object),
            // so we copy back each field individually. If a new field is added to AgentEntryFieldApplier.Apply
            // without a corresponding copy-back line here, the in-memory implementation will silently drop
            // that field's update while the distributed path (which uses the returned record directly) applies
            // it — reproducing the exact divergence this refactor was created to fix.
            // When adding a new field to AgentEntryFieldApplier, always add a copy-back line below.
            var updated = AgentEntryFieldApplier.Apply(entry, field, value);
            entry.ActiveJobId = updated.ActiveJobId;
            entry.ActiveChatSessionId = updated.ActiveChatSessionId;
            entry.OrphanRestoredAt = updated.OrphanRestoredAt;
            entry.LastJobCompletedAt = updated.LastJobCompletedAt;
            entry.Disabled = updated.Disabled;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetLocalSnapshotField(AgentId agentId, string field, string? value)
    {
        // No-op: in-memory path. GetByAgentId returns the live AgentEntry reference directly,
        // so DetectAndRestoreOrphans mutations are already visible to GetByConnectionId.
    }
}
