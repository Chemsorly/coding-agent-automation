using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry.SweepPhases;

/// <summary>
/// Unit tests for <see cref="StaleHeartbeatSweepPhase"/> in isolation.
/// </summary>
public class StaleHeartbeatSweepPhaseTests
{
    private readonly Mock<IAgentRegistryService> _mockRegistry;
    private readonly Mock<ILogger> _mockLogger;
    private readonly StaleHeartbeatSweepPhase _phase;

    public StaleHeartbeatSweepPhaseTests()
    {
        _mockRegistry = new Mock<IAgentRegistryService>();
        _mockLogger = new Mock<ILogger>();
        _phase = new StaleHeartbeatSweepPhase(_mockRegistry.Object, _mockLogger.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static AgentEntry MakeAgent(string agentId, DateTimeOffset lastHeartbeat)
        => new()
        {
            AgentId = agentId,
            ConnectionId = "conn-1",
            Hostname = "host-1",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = lastHeartbeat,
        };

    private static PipelineConfiguration MakeConfig(int heartbeatTimeoutSeconds = 60)
        => new() { HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_FreshHeartbeat_ReturnsFalse_NoTransition()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = MakeAgent("agent-1", now.AddSeconds(-30)); // 30s ago, timeout 60s

        var result = await _phase.ExecuteAsync(agent, now, MakeConfig(60), CancellationToken.None);

        result.Should().BeFalse("agent with fresh heartbeat should not be consumed");
        _mockRegistry.Verify(r => r.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    [Fact]
    public async Task Execute_HeartbeatExactlyAtBoundary_ReturnsFalse_NoTransition()
    {
        // Heartbeat age == timeout (not strictly greater than) → should NOT trigger
        var now = DateTimeOffset.UtcNow;
        var agent = MakeAgent("agent-1", now.AddSeconds(-60)); // exactly 60s = timeout

        var result = await _phase.ExecuteAsync(agent, now, MakeConfig(60), CancellationToken.None);

        result.Should().BeFalse("heartbeat exactly at timeout boundary must not trigger disconnection");
        _mockRegistry.Verify(r => r.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    [Fact]
    public async Task Execute_HeartbeatJustPastTimeout_ReturnsTrue_TransitionsToDisconnected()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = MakeAgent("agent-1", now.AddSeconds(-61)); // 61s > 60s timeout

        var result = await _phase.ExecuteAsync(agent, now, MakeConfig(60), CancellationToken.None);

        result.Should().BeTrue("stale agent must be consumed by this phase");
        _mockRegistry.Verify(r => r.TransitionStatus("agent-1", AgentStatus.Disconnected), Times.Once);
    }

    [Fact]
    public async Task Execute_StaleHeartbeat_WarningLogged()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = MakeAgent("agent-1", now.AddSeconds(-120));

        await _phase.ExecuteAsync(agent, now, MakeConfig(60), CancellationToken.None);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("{AgentId}") && s.Contains("{Age")),
                It.IsAny<AgentId>(),
                It.IsAny<double>()),
            Times.Once);
    }
}
