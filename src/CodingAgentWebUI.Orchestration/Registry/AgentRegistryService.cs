using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

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
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connectionId);

        var now = DateTimeOffset.UtcNow;

        // TODO: If AgentId.Value is null (e.g., from a malformed MessagePack payload producing default(AgentId)),
        // AddOrUpdate will throw NullReferenceException instead of a descriptive ArgumentNullException.
        // Once AgentId's primary constructor validates its input (see TODO in AgentId.cs), this will be safe
        // by construction. Until then, consider adding ArgumentNullException.ThrowIfNull(message.AgentId.Value)
        // here for a cleaner error message.
        var entry = _agents.AddOrUpdate(
            message.AgentId.Value,
            // Add factory — brand new registration
            _ =>
            {
                _logger.Information(
                    "Agent {AgentId} registered (labels=[{Labels}], connection={ConnectionId})",
                    message.AgentId, string.Join(", ", message.Labels), connectionId);

                return new AgentEntry
                {
                    AgentId = message.AgentId.Value,
                    ConnectionId = connectionId,
                    Hostname = message.Hostname,
                    Labels = message.Labels,
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
                    // Remove old connectionId from index before updating
                    _connectionIndex.TryRemove(existing.ConnectionId, out AgentEntry? _);

                    existing.ConnectionId = connectionId;
                    existing.LastHeartbeatAt = now;
                    existing.DisconnectedAt = null;

                    if (existing.Status == AgentStatus.Disconnected)
                    {
                        if (existing.ActiveJobId is not null)
                        {
                            // Agent reconnected with active job — restore to Busy (REQ-3.6)
                            existing.Status = AgentStatus.Busy;
                            // TODO: Add test coverage for BusySince being set during re-registration with active job.
                            // Without this, a reconnecting agent could be spuriously reset by a concurrent status sweep.
                            existing.BusySince = DateTimeOffset.UtcNow;
                            _logger.Information(
                                "Agent {AgentId} re-registered after disconnect with active job {JobId}, status restored to Busy",
                                message.AgentId, existing.ActiveJobId);
                        }
                        else
                        {
                            existing.Status = AgentStatus.Idle;
                            _logger.Information(
                                "Agent {AgentId} re-registered after disconnect, status reset to Idle",
                                message.AgentId);
                        }
                        existing.DisconnectedAt = null;
                    }
                    else
                    {
                        _logger.Information(
                            "Agent {AgentId} re-registered (connection={ConnectionId})",
                            message.AgentId, connectionId);
                    }
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

    /// <summary>
    /// Returns all registered agents regardless of status.
    /// </summary>
    public IReadOnlyList<AgentEntry> GetAllAgents()
    {
        return _agents.Values.ToList().AsReadOnly();
    }

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
}
