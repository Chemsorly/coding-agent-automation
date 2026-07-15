using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for HeartbeatMonitorService — validates sweep logic for stale heartbeats,
/// grace period handling, and disconnected agent cleanup.
/// </summary>
public class HeartbeatMonitorServiceTests : IDisposable
{
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<ILabelSwapper> _mockLabelSwapper;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<ILogger> _mockLogger;
    private readonly HeartbeatMonitorService _monitor;

    public HeartbeatMonitorServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockLabelSwapper = new Mock<ILabelSwapper>();
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        // Default: FailRunAsync returns a dummy run (success path) and simulates the
        // side effects of the real RunLifecycleManager.ClearAgentState — clearing ActiveJobId,
        // OrphanRestoredAt, and transitioning the agent to Idle. This ensures success-path tests
        // validate the full observable state transition, not just mock invocation.
        // Tests that need the race-lost (null) path override this setup.
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .Returns((string runId, string reason, CancellationToken _, FailureReason? __) =>
            {
                // Simulate ClearAgentState: find the agent owning this run and clear its state
                var agents = _registry.GetAllAgents();
                foreach (var agent in agents)
                {
                    if (agent.ActiveJobId == runId)
                    {
                        lock (agent.SyncRoot)
                        {
                            agent.ActiveJobId = null;
                            agent.OrphanRestoredAt = null;
                        }
                        _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                        break;
                    }
                }

                return Task.FromResult<PipelineRun?>(new PipelineRun
                {
                    RunId = runId,
                    IssueIdentifier = "test/repo#0",
                    IssueTitle = "Test",
                    IssueProviderConfigId = "ip-default",
                    RepoProviderConfigId = "rp-default",
                    FailureReason = reason,
                    CurrentStep = PipelineStep.Failed
                });
            });

        var dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _monitor = new HeartbeatMonitorService(
            _registry,
            _runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            _mockConfigStore.Object,
            _mockLogger.Object,
            lifecycleManager: _mockLifecycleManager.Object);
    }

    [Fact]
    public async Task SweepAsync_NoAgents_CompletesWithoutError()
    {
        await _monitor.SweepAsync(CancellationToken.None);

        // No agents, no errors
        _registry.GetAllAgents().Should().BeEmpty();
    }

    [Fact]
    public async Task SweepAsync_FreshHeartbeat_AgentStaysIdle()
    {
        RegisterAgent("agent-1", "conn-1");

        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task SweepAsync_StaleHeartbeat_TransitionsToDisconnected()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-100); // >90s stale

        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Disconnected);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedWithinGracePeriod_AgentRetained()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.FromMinutes(5)
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        // DisconnectedAt is set to now by TransitionStatus — within grace period

        await _monitor.SweepAsync(CancellationToken.None);

        _registry.GetByAgentId("agent-1").Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_DisconnectedPastGracePeriod_NoActiveJob_Deregistered()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero // Immediate expiry
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        await _monitor.SweepAsync(CancellationToken.None);

        _registry.GetByAgentId("agent-1").Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_DisconnectedPastGracePeriod_WithActiveJob_MarksRunFailed()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should be deregistered
        _registry.GetByAgentId("agent-1").Should().BeNull();

        // FailRunAsync should have been called with correct arguments
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1", "Agent disconnected", It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_FreshHeartbeat_StaysBusy()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // Add a run so the agent is legitimately busy
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            LastStepChangeAt = DateTimeOffset.UtcNow,
            RunType = PipelineRunType.Implementation
        });

        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public async Task SweepAsync_MultipleAgents_HandlesEachIndependently()
    {
        var fresh = RegisterAgent("agent-fresh", "conn-1");
        var stale = RegisterAgent("agent-stale", "conn-2");
        stale.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-100);

        await _monitor.SweepAsync(CancellationToken.None);

        _registry.GetByAgentId("agent-fresh")!.Status.Should().Be(AgentStatus.Idle);
        _registry.GetByAgentId("agent-stale")!.Status.Should().Be(AgentStatus.Disconnected);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedAgent_NullDisconnectedAt_Skipped()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        // Manually clear DisconnectedAt to test the null check
        entry.DisconnectedAt = null;

        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should still be in registry (null DisconnectedAt is skipped)
        _registry.GetByAgentId("agent-1").Should().NotBeNull();
    }

    // TODO: This test and SweepAsync_DisconnectedWithActiveReviewRun_CallsFailRunAsync are now functionally
    // identical from HeartbeatMonitorService's perspective — both verify FailRunAsync("job-1", "Agent disconnected", ...).
    // The RunType distinction (Implementation vs Review) only matters inside RunLifecycleManager (label routing).
    // Consider merging into a single test or adding distinguishing assertions.
    [Fact]
    public async Task SweepAsync_DisconnectedWithActiveImplementationRun_CallsFailRunAsync()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.Zero });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1", "Agent disconnected", It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedWithActiveReviewRun_CallsFailRunAsync()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.Zero });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Review PR",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Review
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1", "Agent disconnected", It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_OrphanRestoredBusy_WithinGracePeriod_DoesNotFail()
    {
        // Phase 1.5: Agent is Busy with OrphanRestoredAt set but still within grace period.
        // Simulates a container restart where the orchestrator detected the crash and is waiting
        // for the agent to resume (which it won't, but grace period hasn't expired yet).
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow; // just now — well within 5min grace
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#50",
            IssueTitle = "Stuck run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1"
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Run should still be active (grace period not expired)
        _runService.GetRun("job-1").Should().NotBeNull();
        entry.Status.Should().Be(AgentStatus.Busy);
        entry.OrphanRestoredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_OrphanRestoredBusy_PastGracePeriod_FailsRunAndReturnsToIdle()
    {
        // Phase 1.5: Agent is Busy with OrphanRestoredAt past grace period.
        // This is the core crash-recovery scenario: agent container restarted, re-registered
        // without its active job, orchestrator set OrphanRestoredAt, and now the grace period
        // expired without the agent reporting progress. Run should be failed.
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.FromMinutes(5) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10); // well past 5min
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#50",
            IssueTitle = "Stuck run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1",
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // FailRunAsync should have been called with correct arguments
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1",
            "Agent did not resume orphaned job within grace period",
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // Verify observable state: agent returned to Idle with cleared state
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_OrphanRestoredBusy_NoOrphanRestoredAt_DoesNotTriggerPhase15()
    {
        // Busy agent WITHOUT OrphanRestoredAt (normal operation) should not be affected by Phase 1.5.
        // This ensures the fix doesn't accidentally kill healthy running agents.
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        entry.OrphanRestoredAt = null; // normal busy agent — not crash-recovered
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#50",
            IssueTitle = "Normal run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1"
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Run should still be active (no OrphanRestoredAt means Phase 1.5 skips it)
        _runService.GetRun("job-1").Should().NotBeNull();
        entry.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public async Task SweepAsync_OrphanedRun_AgentNotInRegistry_MarksRunFailedAndSwapsLabel()
    {
        // TODO: This test only verifies FailRunAsync delegation but does not assert downstream observable effects
        // (run removed from active runs, history persisted, label swapped). Consider using a real RunLifecycleManager
        // or verifying the run is no longer in _runService after the mock executes its side-effect callback.
        // Run assigned to an agent that is NOT in the registry (orphaned)
        var run = new PipelineRun
        {
            RunId = "orphan-1",
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Orphaned",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-gone",
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // FailRunAsync should have been called with correct arguments
        _mockLifecycleManager.Verify(l => l.FailRunAsync("orphan-1",
            "Agent deregistered (orphaned run)",
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_RunWithNullAgentId_NotTreatedAsOrphan()
    {
        // Local run with no agent — should NOT be cleaned up by Phase 3
        var run = new PipelineRun
        {
            RunId = "local-1",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "Local",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = null
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Run should still be active
        _runService.GetRun("local-1").Should().NotBeNull();
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweepAsync_RunWithAgentStillInRegistry_NotTreatedAsOrphan()
    {
        // Agent is registered (even if disconnected) — Phase 3 should skip it
        RegisterAgent("agent-alive", "conn-1");
        _registry.TransitionStatus("agent-alive", AgentStatus.Disconnected);

        var run = new PipelineRun
        {
            RunId = "active-1",
            IssueIdentifier = "org/repo#20",
            IssueTitle = "Active",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-alive"
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Run should still be active (not cleaned by Phase 3)
        _runService.GetRun("active-1").Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_ProgressWithinTimeout_StaysBusy()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-10) // Well within 60min default
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should still be busy
        _registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Busy);
        _runService.GetRun("job-1").Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_ProgressTimeoutExceeded_MarksRunFailed()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(30) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-45) // Exceeds 30min timeout
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // FailRunAsync should have been called with reason containing "progress timeout"
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1",
            It.Is<string>(s => s.Contains("progress timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // Verify observable state: agent returned to Idle with cleared state
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    // TODO: This test and SweepAsync_BusyAgent_RunRemovedByConcurrentPath_ResetsToIdle are functionally
    // identical (both set up a Busy agent with a missing run and OrphanRestoredAt=null). Consider merging
    // into a single test with complete assertions.
    // TODO: Add _mockLogger.Verify() to assert that a Warning was logged containing agent ID and job ID
    // (acceptance criteria: "Warning logged with agent ID and stale job ID for operator visibility").
    [Fact]
    public async Task SweepAsync_BusyAgent_ProgressTimeout_NoRun_ResetsToIdle()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-missing";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        entry.BusySince = DateTimeOffset.UtcNow.AddSeconds(-60); // Backdate past grace period

        // No run added for this job — simulates race condition where run was removed concurrently
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent is reset to Idle since its run no longer exists
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_RunRemovedByConcurrentPath_ResetsToIdle()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-vanished";
        entry.OrphanRestoredAt = null;
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        entry.BusySince = DateTimeOffset.UtcNow.AddSeconds(-60); // Backdate past grace period

        // No run exists for this job ID — simulates ReportJobCompleted removing the run
        // but failing to transition agent to Idle (e.g., SignalR exception)
        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_RecentlyBusy_NoRun_SkipsReset()
    {
        // TODO: Add test for the exact bug scenario (Busy with null ActiveJobId + recent BusySince)
        // to guard against regression if Phase 1.6 condition is broadened to include null ActiveJobId.
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-new";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        // BusySince is set to ~now by TransitionStatus — within the 10s grace period

        // No run exists for this job — but agent just became Busy
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should NOT be reset — grace period protects recently-assigned agents
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Busy);
        agent.ActiveJobId.Should().Be("job-new");
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_BusySinceExpired_NoRun_ResetsToIdle()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-stale";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        entry.BusySince = DateTimeOffset.UtcNow.AddSeconds(-60); // Well past grace period

        // TODO: Stale comment — agent has been Busy for 60s (not 30s). Update comment to reflect
        // the actual -60s backdating used after the grace period was increased to 30s.
        // No run exists for this job — and agent has been Busy for 60s (well past 30s grace)
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should be reset — grace period expired, legitimately stuck
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_NullBusySince_NoRun_ResetsToIdle()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-legacy";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        entry.BusySince = null; // Simulate pre-existing agent without BusySince (backward compat)

        // No run exists for this job — and BusySince is null (no grace period)
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should be reset — null BusySince means no grace period protection
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_LongDrainWithinGrace_SkipsReset()
    {
        // Scenario: Drain operation took 20s under load (DB pool exhaustion, SignalR backpressure).
        // Agent has ActiveJobId set but run not found (edge case: null RunId or concurrent removal).
        // BusySince is 20s ago — within the 30s grace window. Agent should NOT be reset.
        // TODO: This test covers the "ActiveJobId set, run not found" sub-race (Phase 1.6 path).
        // The original issue describes the race as ActiveJobId being null between ResolveAgent and
        // AssignJob. That null-ActiveJobId case is implicitly safe (Phase 1.6 condition requires
        // ActiveJobId is not null), but a dedicated test would document this invariant explicitly.
        // See also TODO at SweepAsync_BusyAgent_RecentlyBusy_NoRun_SkipsReset.
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-slow-drain";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        entry.BusySince = DateTimeOffset.UtcNow.AddSeconds(-20); // Within 30s grace period

        // No run exists — simulates edge case where run is not findable
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should NOT be reset — still within grace period
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Busy);
        agent.ActiveJobId.Should().Be("job-slow-drain");
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_BusySinceExpiredPastNewGrace_ResetsToIdle()
    {
        // Scenario: Agent has been Busy for 45s with no run found.
        // This is well past the 30s grace window — agent is genuinely stuck.
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-stuck";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);
        entry.BusySince = DateTimeOffset.UtcNow.AddSeconds(-45); // Past 30s grace period

        // No run exists for this job
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should be reset — grace period expired, genuinely stuck
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    // TODO: This test is a strict subset of SweepAsync_BusyAgent_ProgressTimeoutExceeded_MarksRunFailed —
    // both test the same Phase 1.6 timeout scenario. This one only verifies FailRunAsync was called;
    // the other also checks observable state (agent Idle, ActiveJobId null). Consider removing this
    // weaker duplicate.
    [Fact]
    public async Task SweepAsync_BusyAgent_ProgressTimeout_CallsFailRunAsync()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(5) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "Stuck",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1",
            It.Is<string>(s => s.Contains("progress timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_DefaultLastStepChangeAt_FallsBackToStartedAt_WithinTimeout()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(1) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // LastStepChangeAt = default but StartedAtOffset is recent (within timeout)
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Legacy",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAtOffset = DateTimeOffset.UtcNow.AddSeconds(-30) // Within 1min timeout
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Should NOT be timed out — StartedAtOffset fallback is within timeout
        _registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Busy);
        _runService.GetRun("job-1").Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_DefaultLastStepChangeAt_FallsBackToStartedAt_ExceedsTimeout()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(1) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // LastStepChangeAt = default and StartedAtOffset exceeds timeout
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Legacy",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-2) // Well past 1min timeout
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // FailRunAsync should have been called with reason containing "progress timeout"
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1",
            It.Is<string>(s => s.Contains("progress timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_BothTimestampsDefault_SkipsProgressCheck()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(1) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // Both LastStepChangeAt and StartedAtOffset are default (truly broken data)
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Legacy",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Should NOT be timed out — both timestamps are default, skip with warning
        _registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Busy);
        _runService.GetRun("job-1").Should().NotBeNull();
    }

    /// <summary>
    /// Regression test: PR #804 introduced progress timeout that killed agents legitimately
    /// waiting in RunningQualityGates during ExternalCi polling (45+ min Docker builds).
    /// The fix (heartbeat refreshes LastStepChangeAt when CurrentStep matches) must keep
    /// such agents alive. This test simulates the exact production scenario:
    /// agent enters RunningQualityGates, 50 minutes pass (no step transition), then a
    /// heartbeat arrives with matching CurrentStep — the sweep must NOT kill the run.
    /// </summary>
    [Fact]
    public async Task SweepAsync_BusyAgent_HeartbeatRefreshedProgress_DoesNotTimeout()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(60) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "ExternalCi wait",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = PipelineStep.RunningQualityGates,
            // Simulate: entered step 50 min ago, but heartbeat refreshed it 25s ago
            LastStepChangeAt = DateTimeOffset.UtcNow.AddSeconds(-25)
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Agent must still be busy — heartbeat kept the clock alive
        _registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Busy);
        _runService.GetRun("job-1").Should().NotBeNull();
    }

    /// <summary>
    /// Regression test counterpart: when the agent considers itself idle (CurrentStep=null,
    /// meaning ReportJobCompleted failed), heartbeats should NOT refresh LastStepChangeAt.
    /// The progress timeout must still fire for stuck-in-Busy agents (#788).
    /// </summary>
    [Fact]
    public async Task SweepAsync_BusyAgent_IdleHeartbeat_StillTimesOut()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(30) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Stuck agent",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = PipelineStep.RunningQualityGates,
            // Agent finished locally 45 min ago, ReportJobCompleted failed.
            // Heartbeats continue with CurrentStep=null — LastStepChangeAt is NOT refreshed.
            LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-45)
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // FailRunAsync should have been called — stuck detection still works
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-1",
            It.Is<string>(s => s.Contains("progress timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedPastGrace_RaceLost_StillDeregisters()
    {
        // Phase 2: FailRunAsync returns null (race lost) — agent should still be deregistered
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.Zero });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-lm";
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var run = new PipelineRun
        {
            RunId = "job-lm",
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Lifecycle test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1"
        };
        _runService.AddRun(run);

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: FailRunAsync should have been called with the correct runId and reason
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-lm", "Agent disconnected", It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // Assert: agent should be deregistered after FailRunAsync (even when race lost)
        _registry.GetByAgentId("agent-1").Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_ProgressTimeout_RaceLost_ClearsAgentState()
    {
        // Phase 1.6: FailRunAsync returns null (race lost) — agent state should be cleared defensively
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(5) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-stuck";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-stuck",
            IssueIdentifier = "org/repo#200",
            IssueTitle = "Stuck",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1",
            LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-10) // exceeds 5min timeout
        };
        _runService.AddRun(run);

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: FailRunAsync called with reason containing "progress timeout"
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-stuck",
            It.Is<string>(s => s.Contains("progress timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // Assert: agent state cleared defensively (race-lost path)
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_OrphanedRun_CallsFailRunAsync()
    {
        // Phase 3: orphaned run — agent not in registry
        var run = new PipelineRun
        {
            RunId = "orphan-lm",
            IssueIdentifier = "org/repo#300",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-gone" // not in registry
        };
        _runService.AddRun(run);

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert
        _mockLifecycleManager.Verify(l => l.FailRunAsync("orphan-lm",
            "Agent deregistered (orphaned run)",
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_OrphanRestored_RaceLost_ClearsAgentState()
    {
        // Phase 1.5: FailRunAsync returns null (race lost) — agent state should be cleared defensively
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.FromMinutes(5) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-orphan";
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10); // past 5min grace
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-orphan",
            IssueIdentifier = "org/repo#400",
            IssueTitle = "Orphan resumed",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1"
        };
        _runService.AddRun(run);

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: FailRunAsync called
        _mockLifecycleManager.Verify(l => l.FailRunAsync("job-orphan",
            "Agent did not resume orphaned job within grace period",
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // Assert: agent state cleared defensively (race-lost path)
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_ConsolidationRunExceedsProgressTimeout_FailsRunAndResetsAgent()
    {
        // Arrange: Agent is busy with a consolidation run that has exceeded the progress timeout.
        // The consolidation service should be called to fail the timed-out run.
        var mockConsolidationService = new Mock<IConsolidationService>();
        var dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentBusyProgressTimeout = TimeSpan.FromMinutes(60)
            });

        var monitorWithConsolidation = new HeartbeatMonitorService(
            _registry,
            _runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            _mockConfigStore.Object,
            _mockLogger.Object,
            consolidationService: mockConsolidationService.Object,
            lifecycleManager: _mockLifecycleManager.Object);

        var entry = RegisterAgent("agent-consol", "conn-consol");
        entry.ActiveJobId = "consol-run-1";
        _registry.TransitionStatus("agent-consol", AgentStatus.Busy);

        // IsRunActive returns true — this is a consolidation run
        mockConsolidationService
            .Setup(c => c.IsRunActive("consol-run-1"))
            .Returns(true);

        // GetActiveRunStartedAt returns a time well past the progress timeout
        mockConsolidationService
            .Setup(c => c.GetActiveRunStartedAt("consol-run-1"))
            .Returns(DateTimeOffset.UtcNow.AddHours(-2)); // 2 hours ago, timeout is 1 hour

        await monitorWithConsolidation.SweepAsync(CancellationToken.None);

        // Assert: consolidation run should be failed via UpdateRunAsync
        mockConsolidationService.Verify(c => c.UpdateRunAsync(
            "consol-run-1",
            ConsolidationRunStatus.Failed,
            It.Is<string>(s => s.Contains("timeout")),
            It.IsAny<CancellationToken>(),
            It.IsAny<long>()), Times.Once);

        // Assert: agent should be reset to Idle
        var agent = _registry.GetByAgentId("agent-consol");
        agent!.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();

        monitorWithConsolidation.Dispose();
    }

    [Fact]
    public async Task SweepAsync_ConsolidationRunWithinTimeout_AgentStaysBusy()
    {
        // Arrange: Agent is busy with a consolidation run that is within the progress timeout.
        var mockConsolidationService = new Mock<IConsolidationService>();
        var dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentBusyProgressTimeout = TimeSpan.FromMinutes(60)
            });

        var monitorWithConsolidation = new HeartbeatMonitorService(
            _registry,
            _runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            _mockConfigStore.Object,
            _mockLogger.Object,
            consolidationService: mockConsolidationService.Object,
            lifecycleManager: _mockLifecycleManager.Object);

        var entry = RegisterAgent("agent-consol", "conn-consol");
        entry.ActiveJobId = "consol-run-2";
        _registry.TransitionStatus("agent-consol", AgentStatus.Busy);

        // IsRunActive returns true — this is a consolidation run
        mockConsolidationService
            .Setup(c => c.IsRunActive("consol-run-2"))
            .Returns(true);

        // Started 10 minutes ago — well within 60-minute timeout
        mockConsolidationService
            .Setup(c => c.GetActiveRunStartedAt("consol-run-2"))
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10));

        await monitorWithConsolidation.SweepAsync(CancellationToken.None);

        // Assert: consolidation run should NOT be failed
        mockConsolidationService.Verify(c => c.UpdateRunAsync(
            It.IsAny<string>(),
            It.IsAny<ConsolidationRunStatus>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<long>()), Times.Never);

        // Assert: agent should still be Busy
        var agent = _registry.GetByAgentId("agent-consol");
        agent!.Status.Should().Be(AgentStatus.Busy);
        agent.ActiveJobId.Should().Be("consol-run-2");

        monitorWithConsolidation.Dispose();
    }

    [Fact]
    public async Task SweepAsync_BusyAgentWithRunRemovedByReplay_NoFalseStuckFailure()
    {
        // Scenario: Agent completed a job during a network partition. On reconnection,
        // the buffered ReportJobCompleted was replayed successfully, which removed the run
        // from the run service. When HeartbeatMonitor sweeps, it should NOT produce a
        // false "agent stuck" failure — it should detect that the run is gone (already
        // processed) and reset the agent to Idle without marking anything as Failed.
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentBusyProgressTimeout = TimeSpan.FromMinutes(60)
            });

        var entry = RegisterAgent("agent-partition", "conn-partition");
        entry.ActiveJobId = "completed-during-partition";
        _registry.TransitionStatus("agent-partition", AgentStatus.Busy);
        entry.BusySince = DateTimeOffset.UtcNow.AddSeconds(-60); // Backdate past grace period

        // The run has already been removed by the replayed ReportJobCompleted
        // (simulating: agent reconnected → buffer drained → ReportJobCompleted processed
        //  → run moved to history). The run service has no entry for this jobId.
        _runService.GetRun("completed-during-partition").Should().BeNull(
            "precondition: run was already removed by replayed completion");

        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: HeartbeatMonitor should NOT have called AddRunToHistoryAsync with a Failed status
        // (no false "stuck" failure)
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(It.Is<PipelineRun>(r =>
            r.RunId == "completed-during-partition" &&
            r.CurrentStep == PipelineStep.Failed), It.IsAny<CancellationToken>()), Times.Never);

        // Assert: agent should be transitioned to Idle (run not found → reset)
        var agent = _registry.GetByAgentId("agent-partition");
        agent!.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull("slot should be cleared since the run is gone");
    }

    private AgentEntry RegisterAgent(string agentId, string connectionId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = new[] { "dotnet" }
        }, connectionId);
    }

    public void Dispose()
    {
        _monitor.Dispose();
    }
}
