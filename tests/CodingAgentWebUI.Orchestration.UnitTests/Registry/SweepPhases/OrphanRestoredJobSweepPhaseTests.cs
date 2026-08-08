using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry.SweepPhases;

/// <summary>
/// Unit tests for <see cref="OrphanRestoredJobSweepPhase"/> in isolation.
/// </summary>
public class OrphanRestoredJobSweepPhaseTests : IDisposable
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<ILogger> _mockLogger;
    private readonly OrphanRestoredJobSweepPhase _phase;

    public OrphanRestoredJobSweepPhaseTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();

        // Default: simulate ClearAgentState side effects on successful FailRunAsync.
        // On the success path, FailRunAsync internally calls ClearAgentState — this mock
        // simulates that to allow asserting on agent state after a successful call.
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
                        lock (a.SyncRoot)
                        {
                            a.ActiveJobId = null;
                            a.OrphanRestoredAt = null;
                        }
                        _registry.TransitionStatus(a.AgentId, AgentStatus.Idle);
                        break;
                    }
                }
                return Task.FromResult<PipelineRun?>(new PipelineRun
                {
                    RunId = runId.Value,
                    IssueIdentifier = "test/repo#0",
                    IssueTitle = "Test",
                    IssueProviderConfigId = "ip-1",
                    RepoProviderConfigId = "rp-1",
                    FailureReason = reason,
                });
            });

        _phase = new OrphanRestoredJobSweepPhase(_registry, _mockLifecycleManager.Object, _mockLogger.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private AgentEntry RegisterBusyAgent(string agentId, string? jobId = "job-1")
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = [],
            ActiveJob = null
        }, "conn-1");
        entry.ActiveJobId = jobId;
        _registry.TransitionStatus(agentId, AgentStatus.Busy);
        return entry;
    }

    private static PipelineConfiguration MakeConfig(TimeSpan gracePeriod)
        => new() { AgentDisconnectGracePeriod = gracePeriod };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_AgentNotBusy_ReturnsFalse()
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host-1", Labels = [], ActiveJob = null
        }, "conn-1");
        // Status is Idle by default

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        result.Should().BeFalse("non-Busy agents must not be processed by this phase");
    }

    [Fact]
    public async Task Execute_BusyAgentWithoutOrphanRestoredAt_ReturnsFalse()
    {
        var entry = RegisterBusyAgent("agent-1");
        entry.OrphanRestoredAt = null;

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        result.Should().BeFalse("agent without OrphanRestoredAt must not be processed by this phase");
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_OrphanRestoredAtWithinGrace_ReturnsTrueWithoutFailingRun()
    {
        // Critical: must return true even when no action is taken, to prevent ProgressTimeoutSweepPhase
        // from also running on this agent.
        var entry = RegisterBusyAgent("agent-1");
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow; // just now — within 5min grace

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        result.Should().BeTrue("within-grace agent must be consumed to prevent progress timeout from also running");
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never,
            "FailRunAsync must not be called within grace period");
        entry.Status.Should().Be(AgentStatus.Busy, "agent status must be unchanged within grace period");
    }

    [Fact]
    public async Task Execute_OrphanRestoredAtPastGrace_ActiveJobSet_FailsRunAndAgentBecomesIdle()
    {
        // The mock simulates ClearAgentState — after FailRunAsync returns non-null,
        // the agent state should be cleared (as the real LifecycleManager would do).
        // TODO: [WARNING] The state assertions below (agent.Status == Idle, ActiveJobId == null,
        // OrphanRestoredAt == null) verify that the mock behaved correctly, not that the phase itself
        // does something on the success path. The production OrphanRestoredJobSweepPhase does NOT
        // explicitly clear ActiveJobId or OrphanRestoredAt on success — it relies entirely on
        // ClearAgentState inside FailRunAsync. If the real FailRunAsync ever stops calling
        // ClearAgentState (interface change, refactor), the phase would leave the agent in Busy with
        // a stale ActiveJobId and these assertions would still pass. Consider an integration-level test
        // or a contract test that verifies ClearAgentState is called when FailRunAsync returns non-null.
        var entry = RegisterBusyAgent("agent-1", "job-1");
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10); // well past 5min grace

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        result.Should().BeTrue();
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync("job-1", "Agent did not resume orphaned job within grace period", It.IsAny<CancellationToken>(), FailureReason.InfrastructureFailure),
            Times.Once);
        // State assertions rely on mock simulating ClearAgentState side effects
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task Execute_OrphanRestoredAtPastGrace_NullActiveJobId_NoFailRunCalled()
    {
        // Guard: if orphanedJobId is null, FailRunAsync must not be called.
        var entry = RegisterBusyAgent("agent-1", jobId: null);
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        result.Should().BeTrue();
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_RaceLost_PhaseExplicitlyClearsAgentState()
    {
        // When FailRunAsync returns null (race lost), the phase itself must clear agent state —
        // not rely on the lifecycle manager (which already processed the run via another path).
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var entry = RegisterBusyAgent("agent-1", "job-1");
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(5)), CancellationToken.None);

        // Phase must explicitly clear state on race-lost path
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle, "phase must call TransitionStatus(Idle) on race-lost path");
        agent.ActiveJobId.Should().BeNull("phase must clear ActiveJobId on race-lost path");
        agent.OrphanRestoredAt.Should().BeNull("phase must clear OrphanRestoredAt on race-lost path");
    }

    public void Dispose()
    {
        // AgentRegistryService doesn't implement IDisposable — nothing to dispose.
    }
}
