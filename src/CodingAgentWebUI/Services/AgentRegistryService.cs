using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// In-memory registry of connected agents. Tracks agent status, heartbeats,
/// and active job assignments. Registered as a singleton in DI.
/// </summary>
public sealed class AgentRegistryService
{
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
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

        var entry = _agents.AddOrUpdate(
            message.AgentId,
            // Add factory — brand new registration
            _ =>
            {
                _logger.Information(
                    "Agent {AgentId} registered (type={AgentType}, labels=[{Labels}], connection={ConnectionId})",
                    message.AgentId, message.AgentType, string.Join(", ", message.Labels), connectionId);

                return new AgentEntry
                {
                    AgentId = message.AgentId,
                    ConnectionId = connectionId,
                    Hostname = message.Hostname,
                    AgentType = message.AgentType,
                    Labels = message.Labels,
                    Status = AgentStatus.Idle,
                    RegisteredAt = now,
                    LastHeartbeatAt = now
                };
            },
            // Update factory — re-registration (reconnection)
            (_, existing) =>
            {
                existing.ConnectionId = connectionId;
                existing.LastHeartbeatAt = now;
                existing.DisconnectedAt = null;

                if (existing.Status == AgentStatus.Disconnected)
                {
                    if (existing.ActiveJobId is not null)
                    {
                        // Agent reconnected with active job — restore to Busy (REQ-3.6)
                        existing.Status = AgentStatus.Busy;
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

                return existing;
            });

        return entry;
    }

    /// <summary>
    /// Removes an agent from the registry entirely.
    /// </summary>
    public bool Deregister(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        if (_agents.TryRemove(agentId, out var removed))
        {
            _logger.Information("Agent {AgentId} deregistered", agentId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Looks up an agent by its unique agent identifier.
    /// </summary>
    public AgentEntry? GetByAgentId(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return _agents.TryGetValue(agentId, out var entry) ? entry : null;
    }

    /// <summary>
    /// Looks up an agent by its current SignalR connection ID.
    /// </summary>
    public AgentEntry? GetByConnectionId(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        return _agents.Values.FirstOrDefault(a => a.ConnectionId == connectionId);
    }

    /// <summary>
    /// Updates the heartbeat timestamp for the specified agent.
    /// </summary>
    public void UpdateHeartbeat(string agentId, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        if (_agents.TryGetValue(agentId, out var entry))
        {
            entry.LastHeartbeatAt = timestamp;
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
    public void TransitionStatus(string agentId, AgentStatus newStatus)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        if (_agents.TryGetValue(agentId, out var entry))
        {
            var oldStatus = entry.Status;
            entry.Status = newStatus;

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
}
