using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Property-based tests for AgentRegistryService: heartbeat timeout, disconnect/reconnect,
/// and agent selection.
/// </summary>
public class AgentRegistryServicePropertyTests
{
    private static AgentRegistryService CreateRegistry() =>
        new(new Mock<ILogger>().Object);

    private static AgentEntry RegisterAgent(AgentRegistryService registry, string agentId, string connectionId, IReadOnlyList<string>? labels = null)
    {
        return registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = labels ?? new[] { "kiro", "dotnet" }
        }, connectionId);
    }

    /// <summary>
    /// Property 4: Unregistered Agent Message Rejection
    /// For any agentId not in registry, GetByConnectionId returns null.
    /// **Validates: Requirements 1.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void UnregisteredAgent_ReturnsNull(NonEmptyString connectionId)
    {
        var registry = CreateRegistry();

        registry.GetByConnectionId(connectionId.Get).Should().BeNull();
    }

    /// <summary>
    /// Property 6: Heartbeat Timeout Detection
    /// Receiving heartbeat updates LastHeartbeatAt.
    /// **Validates: Requirements 3.3, 3.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Heartbeat_UpdatesLastHeartbeatAt(NonEmptyString agentId)
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, agentId.Get, "conn-1");

        var newTimestamp = DateTimeOffset.UtcNow.AddMinutes(5);
        registry.UpdateHeartbeat(agentId.Get, newTimestamp);

        var entry = registry.GetByAgentId(agentId.Get);
        entry.Should().NotBeNull();
        entry!.LastHeartbeatAt.Should().Be(newTimestamp);
    }

    /// <summary>
    /// Property 7: Disconnect/Reconnect State Machine
    /// Connection loss → Disconnected + DisconnectedAt set.
    /// **Validates: Requirements 2.6, 3.6, 3.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Disconnect_SetsDisconnectedStatus(NonEmptyString agentId)
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, agentId.Get, "conn-1");

        registry.TransitionStatus(agentId.Get, AgentStatus.Disconnected);

        var entry = registry.GetByAgentId(agentId.Get);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(AgentStatus.Disconnected);
        entry.DisconnectedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Property 7 (continued): Reconnect within grace period resets to Idle.
    /// **Validates: Requirements 2.6, 3.6, 3.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Reconnect_AfterDisconnect_ResetsToIdle(NonEmptyString agentId, NonEmptyString newConn)
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, agentId.Get, "conn-1");
        registry.TransitionStatus(agentId.Get, AgentStatus.Disconnected);

        // Re-register (reconnect)
        var entry = RegisterAgent(registry, agentId.Get, newConn.Get);

        entry.Status.Should().Be(AgentStatus.Idle);
        entry.DisconnectedAt.Should().BeNull();
        entry.ConnectionId.Should().Be(newConn.Get);
    }

    /// <summary>
    /// Property 7 (continued): Grace period expiry without active job → removed from registry.
    /// **Validates: Requirements 2.6, 3.6, 3.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Deregister_RemovesFromRegistry(NonEmptyString agentId)
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, agentId.Get, "conn-1");
        registry.TransitionStatus(agentId.Get, AgentStatus.Disconnected);

        registry.Deregister(agentId.Get);

        registry.GetByAgentId(agentId.Get).Should().BeNull();
        registry.GetAllAgents().Should().BeEmpty();
    }

    /// <summary>
    /// Property 8: Agent Selection FIFO Ordering
    [Property(MaxTest = 20)]
    public void JobAcceptance_TransitionsToBusy(NonEmptyString agentId, NonEmptyString jobId)
    {
        var registry = CreateRegistry();
        var entry = RegisterAgent(registry, agentId.Get, "conn-1");

        entry.ActiveJobId = jobId.Get;
        registry.TransitionStatus(agentId.Get, AgentStatus.Busy);

        var updated = registry.GetByAgentId(agentId.Get);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(AgentStatus.Busy);
        updated.ActiveJobId.Should().Be(jobId.Get);
    }

    /// <summary>
    /// Property 13: Job Completion State Transition
    /// Agent transitions Busy → Idle, ActiveJobId cleared.
    /// **Validates: Requirements 6.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void JobCompletion_TransitionsToIdle(NonEmptyString agentId, NonEmptyString jobId)
    {
        var registry = CreateRegistry();
        var entry = RegisterAgent(registry, agentId.Get, "conn-1");
        entry.ActiveJobId = jobId.Get;
        registry.TransitionStatus(agentId.Get, AgentStatus.Busy);

        // Complete the job
        entry.ActiveJobId = null;
        entry.LastJobCompletedAt = DateTimeOffset.UtcNow;
        registry.TransitionStatus(agentId.Get, AgentStatus.Idle);

        var updated = registry.GetByAgentId(agentId.Get);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(AgentStatus.Idle);
        updated.ActiveJobId.Should().BeNull();
    }
}
