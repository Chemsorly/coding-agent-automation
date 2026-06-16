using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for AgentRegistryService registration invariants.
/// </summary>
public class AgentRegistryPropertyTests
{
    private static AgentRegistryService CreateRegistry()
    {
        return new AgentRegistryService(new Mock<ILogger>().Object);
    }

    /// <summary>
    /// Property 1: Agent Registration Invariant
    /// For any valid AgentRegistrationMessage, registering produces exactly one entry
    /// with status Idle. Re-registering same agentId with different ConnectionId updates
    /// entry (no duplicate), resets status to Idle if Disconnected.
    /// **Validates: Requirements 2.2, 2.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Registration_ProducesExactlyOneIdleEntry(NonEmptyString agentId, NonEmptyString hostname, NonEmptyString connectionId)
    {
        var registry = CreateRegistry();
        var message = new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = hostname.Get,
            Labels = new[] { "kiro", "dotnet" }
        };

        var entry = registry.Register(message, connectionId.Get);

        // Exactly one entry exists
        registry.GetAllAgents().Should().HaveCount(1);
        entry.Status.Should().Be(AgentStatus.Idle);
        entry.AgentId.Should().Be(agentId.Get);
        entry.ConnectionId.Should().Be(connectionId.Get);
    }

    /// <summary>
    /// Property 1 (continued): Re-registering same agentId with different ConnectionId
    /// updates entry without creating a duplicate.
    /// **Validates: Requirements 2.2, 2.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ReRegistration_UpdatesConnectionId_NoDuplicate(NonEmptyString agentId, NonEmptyString conn1, NonEmptyString conn2)
    {
        var registry = CreateRegistry();
        var message = new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = "host1",
            Labels = new[] { "kiro" }
        };

        registry.Register(message, conn1.Get);
        var entry = registry.Register(message, conn2.Get);

        // Still exactly one entry — no duplicate
        registry.GetAllAgents().Should().HaveCount(1);
        entry.ConnectionId.Should().Be(conn2.Get);
    }

    /// <summary>
    /// Property 1 (continued): Re-registering a Disconnected agent resets status to Idle.
    /// **Validates: Requirements 2.2, 2.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ReRegistration_ResetsDisconnectedToIdle(NonEmptyString agentId, NonEmptyString conn1, NonEmptyString conn2)
    {
        var registry = CreateRegistry();
        var message = new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = "host1",
            Labels = new[] { "kiro" }
        };

        registry.Register(message, conn1.Get);
        registry.TransitionStatus(agentId.Get, AgentStatus.Disconnected);

        // Verify it's disconnected
        registry.GetByAgentId(agentId.Get)!.Status.Should().Be(AgentStatus.Disconnected);

        // Re-register with new connection
        var entry = registry.Register(message, conn2.Get);

        entry.Status.Should().Be(AgentStatus.Idle);
        entry.ConnectionId.Should().Be(conn2.Get);
        entry.DisconnectedAt.Should().BeNull();
        registry.GetAllAgents().Should().HaveCount(1);
    }
}
