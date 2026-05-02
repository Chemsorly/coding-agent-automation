using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Extended unit tests for AgentRegistryService — covers re-registration,
/// status transitions, heartbeat updates, and concurrent access patterns.
/// </summary>
public class AgentRegistryServiceExtendedTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger;

    public AgentRegistryServiceExtendedTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
    }

    // ── Constructor ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentRegistryService(null!));
    }

    // ── Register ────────────────────────────────────────────────────────

    [Fact]
    public void Register_NewAgent_SetsIdleStatus()
    {
        var entry = RegisterAgent("agent-1", "conn-1");

        entry.Status.Should().Be(AgentStatus.Idle);
        entry.AgentId.Should().Be("agent-1");
        entry.ConnectionId.Should().Be("conn-1");
    }

    [Fact]
    public void Register_NullMessage_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Register(null!, "conn-1"));
    }

    [Fact]
    public void Register_NullConnectionId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Register(
            new AgentRegistrationMessage { AgentId = "a", Hostname = "h", AgentType = "t", Labels = new[] { "l" } },
            null!));
    }

    [Fact]
    public void Register_ReRegistration_UpdatesConnectionId()
    {
        RegisterAgent("agent-1", "conn-1");
        var entry = RegisterAgent("agent-1", "conn-2");

        entry.ConnectionId.Should().Be("conn-2");
    }

    [Fact]
    public void Register_ReRegistration_AfterDisconnect_ResetsToIdle()
    {
        RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);

        var entry = RegisterAgent("agent-1", "conn-2");

        entry.Status.Should().Be(AgentStatus.Idle);
        entry.DisconnectedAt.Should().BeNull();
    }

    [Fact]
    public void Register_ReRegistration_AfterDisconnect_WithActiveJob_RestoresToBusy()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);

        var reregistered = RegisterAgent("agent-1", "conn-2");

        reregistered.Status.Should().Be(AgentStatus.Busy);
        reregistered.ActiveJobId.Should().Be("job-1");
    }

    [Fact]
    public void Register_SetsLastHeartbeatAt()
    {
        var before = DateTimeOffset.UtcNow;
        var entry = RegisterAgent("agent-1", "conn-1");

        entry.LastHeartbeatAt.Should().BeOnOrAfter(before);
    }

    // ── Deregister ──────────────────────────────────────────────────────

    [Fact]
    public void Deregister_ExistingAgent_ReturnsTrue()
    {
        RegisterAgent("agent-1", "conn-1");

        _registry.Deregister("agent-1").Should().BeTrue();
        _registry.GetByAgentId("agent-1").Should().BeNull();
    }

    [Fact]
    public void Deregister_NonExistentAgent_ReturnsFalse()
    {
        _registry.Deregister("non-existent").Should().BeFalse();
    }

    [Fact]
    public void Deregister_NullAgentId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Deregister(null!));
    }

    // ── GetByAgentId ────────────────────────────────────────────────────

    [Fact]
    public void GetByAgentId_NullId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.GetByAgentId(null!));
    }

    [Fact]
    public void GetByAgentId_NonExistent_ReturnsNull()
    {
        _registry.GetByAgentId("non-existent").Should().BeNull();
    }

    // ── GetByConnectionId ───────────────────────────────────────────────

    [Fact]
    public void GetByConnectionId_NullId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.GetByConnectionId(null!));
    }

    [Fact]
    public void GetByConnectionId_NonExistent_ReturnsNull()
    {
        _registry.GetByConnectionId("non-existent").Should().BeNull();
    }

    // ── UpdateHeartbeat ─────────────────────────────────────────────────

    [Fact]
    public void UpdateHeartbeat_ExistingAgent_UpdatesTimestamp()
    {
        RegisterAgent("agent-1", "conn-1");
        var newTimestamp = DateTimeOffset.UtcNow.AddMinutes(5);

        _registry.UpdateHeartbeat("agent-1", newTimestamp);

        var agent = _registry.GetByAgentId("agent-1");
        agent!.LastHeartbeatAt.Should().Be(newTimestamp);
    }

    [Fact]
    public void UpdateHeartbeat_NonExistentAgent_DoesNotThrow()
    {
        // Should log warning but not throw
        _registry.UpdateHeartbeat("non-existent", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void UpdateHeartbeat_NullAgentId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.UpdateHeartbeat(null!, DateTimeOffset.UtcNow));
    }

    // ── TransitionStatus ────────────────────────────────────────────────

    [Fact]
    public void TransitionStatus_ToDisconnected_SetsDisconnectedAt()
    {
        RegisterAgent("agent-1", "conn-1");

        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);

        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Disconnected);
        agent.DisconnectedAt.Should().NotBeNull();
    }

    [Fact]
    public void TransitionStatus_ToIdle_ClearsDisconnectedAt()
    {
        RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        _registry.TransitionStatus("agent-1", AgentStatus.Idle);

        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Idle);
        agent.DisconnectedAt.Should().BeNull();
    }

    [Fact]
    public void TransitionStatus_ToBusy_DoesNotAffectDisconnectedAt()
    {
        RegisterAgent("agent-1", "conn-1");

        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Busy);
        agent.DisconnectedAt.Should().BeNull();
    }

    [Fact]
    public void TransitionStatus_NonExistentAgent_DoesNotThrow()
    {
        _registry.TransitionStatus("non-existent", AgentStatus.Idle);
    }

    [Fact]
    public void TransitionStatus_NullAgentId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.TransitionStatus(null!, AgentStatus.Idle));
    }

    // ── GetIdleAgents ───────────────────────────────────────────────────

    [Fact]
    public void GetIdleAgents_ReturnsOnlyIdleAgents()
    {
        RegisterAgent("agent-1", "conn-1");
        RegisterAgent("agent-2", "conn-2");
        _registry.TransitionStatus("agent-2", AgentStatus.Busy);

        var idle = _registry.GetIdleAgents();

        idle.Should().HaveCount(1);
        idle[0].AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void GetIdleAgents_WhenEmpty_ReturnsEmptyList()
    {
        _registry.GetIdleAgents().Should().BeEmpty();
    }

    // ── GetAllAgents ────────────────────────────────────────────────────

    [Fact]
    public void GetAllAgents_ReturnsAllRegardlessOfStatus()
    {
        RegisterAgent("agent-1", "conn-1");
        RegisterAgent("agent-2", "conn-2");
        _registry.TransitionStatus("agent-2", AgentStatus.Disconnected);

        var all = _registry.GetAllAgents();

        all.Should().HaveCount(2);
    }

    [Fact]
    public void GetAllAgents_WhenEmpty_ReturnsEmptyList()
    {
        _registry.GetAllAgents().Should().BeEmpty();
    }

    // ── Concurrent access ───────────────────────────────────────────────

    [Fact]
    public void ConcurrentRegistration_IsThreadSafe()
    {
        Parallel.For(0, 50, i =>
        {
            _registry.Register(new AgentRegistrationMessage
            {
                AgentId = $"agent-{i}",
                Hostname = $"host-{i}",
                AgentType = "test",
                Labels = new[] { "test" }
            }, $"conn-{i}");
        });

        _registry.GetAllAgents().Should().HaveCount(50);
    }

    [Fact]
    public void ConcurrentHeartbeatUpdates_IsThreadSafe()
    {
        RegisterAgent("agent-1", "conn-1");

        Parallel.For(0, 100, i =>
        {
            _registry.UpdateHeartbeat("agent-1", DateTimeOffset.UtcNow.AddSeconds(i));
        });

        var agent = _registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private AgentEntry RegisterAgent(string agentId, string connectionId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            AgentType = "kiro-dotnet",
            Labels = new[] { "dotnet", "linux" }
        }, connectionId);
    }
}
