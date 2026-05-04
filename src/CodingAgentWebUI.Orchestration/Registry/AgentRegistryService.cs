using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// In-memory registry of connected agents. Tracks agent status, heartbeats,
/// and active job assignments. Registered as a singleton in DI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design Decision: Intentionally non-sealed.</b>
/// This class is non-sealed to allow E2E test subclasses (specifically
/// <c>ResettableAgentRegistryService</c> in
/// <c>tests/CodingAgentWebUI.E2ETests/Infrastructure/ResettableServices.cs</c>)
/// to inherit and expose a <c>Reset()</c> method for test isolation.
/// </para>
/// <para>
/// <b>Sealed + Composition vs Non-Sealed + Inheritance Tradeoff:</b>
/// The preferred .NET pattern is to seal classes by default and use composition-based
/// test doubles (e.g., wrapper/decorator pattern with extracted interfaces). The current
/// non-sealed + inheritance approach was chosen for pragmatic E2E test state reset without
/// polluting the production API with reset methods. Migration to sealed + composition
/// requires: (1) extracting an interface (e.g., <c>IAgentRegistryService</c>),
/// (2) updating E2E tests to use a wrapper/decorator that delegates to the real service
/// and adds reset capability, and (3) verifying no production code relies on inheritance.
/// This migration is documented as a future improvement — see Requirement 22.
/// </para>
/// </remarks>
public class AgentRegistryService
{
    /// <summary>
    /// Backing store for registered agents. Exposed as <c>protected</c> to allow
    /// E2E test subclasses (e.g., <c>ResettableAgentRegistryService</c>) to clear state
    /// between tests via <c>_agents.Clear()</c>.
    /// </summary>
    /// <remarks>
    /// The preferred .NET pattern for test access is <c>internal</c> visibility combined with
    /// <c>[InternalsVisibleTo]</c> in the <c>.csproj</c>. The <c>protected</c> modifier is used
    /// here because the E2E test subclass pattern requires inheritance-based access. If migrating
    /// to sealed + composition, this field should become <c>private</c>.
    /// </remarks>
    protected readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
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
