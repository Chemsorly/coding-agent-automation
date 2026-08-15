using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry.SweepPhases;

/// <summary>
/// Unit tests for <see cref="DisconnectedAgentSweepPhase"/> in isolation.
/// </summary>
public class DisconnectedAgentSweepPhaseTests : IDisposable
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<ILogger> _mockLogger;
    private readonly DisconnectedAgentSweepPhase _phase;

    public DisconnectedAgentSweepPhaseTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();

        // Default: FailRunAsync succeeds and simulates ClearAgentState side effects.
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .Returns((RunId runId, string reason, CancellationToken _, FailureReason? __) =>
            {
                foreach (var a in _registry.GetAllAgents())
                {
                    if (a.ActiveJobId == runId.Value)
                    {
                        lock (a.SyncRoot) { a.ActiveJobId = null; }
                        _registry.TransitionStatus(a.AgentId, AgentStatus.Idle);
                        break;
                    }
                }
                return Task.FromResult<PipelineRun?>(new PipelineRun
                {
                    RunId = runId.Value, IssueIdentifier = "test/repo#0", IssueTitle = "Test",
                    IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1", FailureReason = reason,
                });
            });

        _phase = new DisconnectedAgentSweepPhase(_registry, _mockLifecycleManager.Object, _mockLogger.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private AgentEntry RegisterDisconnectedAgent(string agentId, DateTimeOffset? disconnectedAt = null, string? jobId = null)
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId, Hostname = $"host-{agentId}", Labels = [], ActiveJob = null
        }, "conn-1");
        if (jobId is not null)
            entry.ActiveJobId = jobId;
        _registry.TransitionStatus(agentId, AgentStatus.Disconnected);
        if (disconnectedAt.HasValue)
            entry.DisconnectedAt = disconnectedAt.Value;
        return entry;
    }

    private static PipelineConfiguration MakeConfig(TimeSpan gracePeriod)
        => new() { AgentDisconnectGracePeriod = gracePeriod };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_AgentNotDisconnected_ReturnsFalse()
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host-1", Labels = [], ActiveJob = null
        }, "conn-1");
        // Status = Idle

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.Zero), CancellationToken.None);

        result.Should().BeFalse("non-Disconnected agent must not be processed by this phase");
    }

    [Fact]
    public async Task Execute_DisconnectedWithNullDisconnectedAt_ReturnsTrueNoDeregister()
    {
        var entry = RegisterDisconnectedAgent("agent-1");
        entry.DisconnectedAt = null; // simulate missing timestamp

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.Zero), CancellationToken.None);

        result.Should().BeTrue("agent is consumed even when DisconnectedAt is null");
        _registry.GetByAgentId("agent-1").Should().NotBeNull("agent with null DisconnectedAt must not be deregistered");
    }

    [Fact]
    public async Task Execute_DisconnectedWithinGracePeriod_NotDeregistered()
    {
        var entry = RegisterDisconnectedAgent("agent-1", DateTimeOffset.UtcNow);
        // DisconnectedAt = now, grace = 5min → still within grace

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        result.Should().BeTrue();
        _registry.GetByAgentId("agent-1").Should().NotBeNull("agent within disconnect grace must not be deregistered");
    }

    [Fact]
    public async Task Execute_DisconnectedPastGrace_NoActiveJob_DeregisteredWithInfoLog()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterDisconnectedAgent("agent-1", now.AddMinutes(-10));
        // No active job

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.Zero), CancellationToken.None);

        _registry.GetByAgentId("agent-1").Should().BeNull("agent past grace with no job must be deregistered");
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_DisconnectedPastGrace_WithActiveJob_FailsRunThenDeregisters()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterDisconnectedAgent("agent-1", now.AddMinutes(-10), jobId: "job-1");

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.Zero), CancellationToken.None);

        // TODO: [WARNING] The string "job-1" passed to Verify relies on an implicit conversion from string
        // to RunId that Moq uses for argument matching. If the implicit conversion is removed or RunId
        // changes, Moq would silently find no matching invocation and pass Times.Never semantics instead
        // of failing. Use It.Is<RunId>(r => r.Value == "job-1") for unambiguous matching. The same
        // pattern exists in OrphanRestoredJobSweepPhaseTests and OrphanedRunSweepPhaseTests.
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync("job-1", "Agent disconnected", It.IsAny<CancellationToken>(), FailureReason.InfrastructureFailure),
            Times.Once);
        _registry.GetByAgentId("agent-1").Should().BeNull("agent must be deregistered after run failure");
    }

    [Fact]
    public async Task Execute_DisconnectedPastGrace_WithActiveJob_WarningLoggedWithJobId()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterDisconnectedAgent("agent-1", now.AddMinutes(-10), jobId: "job-1");

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.Zero), CancellationToken.None);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("{AgentId}") && s.Contains("{JobId}")),
                It.IsAny<AgentId>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_RaceLost_ActiveJobIdClearedAndAgentDeregistered()
    {
        // When FailRunAsync returns null (race lost), phase clears ActiveJobId defensively
        // and still deregisters the agent.
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var now = DateTimeOffset.UtcNow;
        var entry = RegisterDisconnectedAgent("agent-1", now.AddMinutes(-10), jobId: "job-1");

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.Zero), CancellationToken.None);

        // Agent deregistered even on race-lost
        _registry.GetByAgentId("agent-1").Should().BeNull("agent must still be deregistered on race-lost path");
        entry.ActiveJobId.Should().BeNull("ActiveJobId must be cleared on race-lost path");
    }

    public void Dispose() { }
}
