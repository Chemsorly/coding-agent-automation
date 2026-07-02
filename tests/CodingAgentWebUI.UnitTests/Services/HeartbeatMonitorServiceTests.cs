using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
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
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<ILabelSwapper> _mockLabelSwapper;
    private readonly Mock<ILogger> _mockLogger;
    private readonly HeartbeatMonitorService _monitor;

    public HeartbeatMonitorServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockLabelSwapper = new Mock<ILabelSwapper>();

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _monitor = new HeartbeatMonitorService(
            _registry,
            _runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            _mockConfigStore.Object,
            _mockLogger.Object);
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
        _mockConfigStore
            .Setup(c => c.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

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

        // Run should be removed from active runs
        _runService.GetRun("job-1").Should().BeNull();

        // History should have been called
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.Is<PipelineRun>(r =>
            r.RunId == "job-1" &&
            r.FailureReason == "Agent disconnected" &&
            r.CurrentStep == PipelineStep.Failed)), Times.Once);
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

    [Fact]
    public async Task SweepAsync_DisconnectedWithActiveImplementationRun_SwapsLabelViaIssueProvider()
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

        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedWithActiveReviewRun_SwapsLabelViaRepoProvider()
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

        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "rp-1", "org/repo#42", AgentLabels.Error, LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()), Times.Once);
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

        // Run should be removed from active runs and marked failed
        _runService.GetRun("job-1").Should().BeNull();
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.Is<PipelineRun>(r =>
            r.RunId == "job-1" &&
            r.FailureReason == "Agent did not resume orphaned job within grace period" &&
            r.CurrentStep == PipelineStep.Failed)), Times.Once);

        // Agent should be returned to Idle
        entry.Status.Should().Be(AgentStatus.Idle);
        entry.ActiveJobId.Should().BeNull();
        entry.OrphanRestoredAt.Should().BeNull();

        // Label should be swapped to error
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#50", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
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

        // Run should be removed from active runs
        _runService.GetRun("orphan-1").Should().BeNull();

        // History should have been called with correct failure reason
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.Is<PipelineRun>(r =>
            r.RunId == "orphan-1" &&
            r.FailureReason == "Agent deregistered (orphaned run)" &&
            r.CurrentStep == PipelineStep.Failed)), Times.Once);

        // Label should be swapped to error via issue provider
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#99", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
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
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.IsAny<PipelineRun>()), Times.Never);
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

        // Agent should be idle
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();

        // Run should be removed from active runs
        _runService.GetRun("job-1").Should().BeNull();

        // History should have been called with progress timeout failure reason
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.Is<PipelineRun>(r =>
            r.RunId == "job-1" &&
            r.FailureReason!.Contains("progress timeout") &&
            r.CurrentStep == PipelineStep.Failed)), Times.Once);
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

        // No run exists for this job ID — simulates ReportJobCompleted removing the run
        // but failing to transition agent to Idle (e.g., SignalR exception)
        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_ProgressTimeout_SwapsLabelToError()
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

        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#10", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_DefaultLastStepChangeAt_SkipsProgressCheck()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(1) });

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // LastStepChangeAt = default (pre-existing run without the new field)
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

        // Should NOT be timed out — default value guard protects legacy runs
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

        // Agent should be killed — stuck detection still works
        _registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Idle);
        _runService.GetRun("job-1").Should().BeNull();
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.Is<PipelineRun>(r =>
            r.RunId == "job-1" &&
            r.FailureReason!.Contains("progress timeout"))), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedPastGrace_WithLifecycleManager_CallsFailRunAsync()
    {
        // Arrange: HeartbeatMonitor constructed WITH IRunLifecycleManager should delegate to it
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null);

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.Zero });

        var registry = new AgentRegistryService(_mockLogger.Object);
        var runService = new OrchestratorRunService(_mockLogger.Object);
        var dispatcher = new JobDispatcherService(registry, _mockLogger.Object);

        var monitor = new HeartbeatMonitorService(
            registry,
            runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            mockConfigStore.Object,
            _mockLogger.Object,
            lifecycleManager: mockLifecycle.Object);

        var entry = registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-lm",
            Hostname = "host-lm",
            Labels = new[] { "dotnet" }
        }, "conn-lm");
        entry.ActiveJobId = "job-lm";
        registry.TransitionStatus("agent-lm", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var run = new PipelineRun
        {
            RunId = "job-lm",
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Lifecycle test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-lm"
        };
        runService.AddRun(run);

        // Act
        await monitor.SweepAsync(CancellationToken.None);

        // Assert: FailRunAsync should have been called with the correct runId and reason
        mockLifecycle.Verify(l => l.FailRunAsync("job-lm", "Agent disconnected", It.IsAny<CancellationToken>()), Times.Once);

        // The old manual path should NOT have been taken (history not called directly)
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.IsAny<PipelineRun>()), Times.Never);

        // Assert: agent should be deregistered after FailRunAsync
        registry.GetByAgentId("agent-lm").Should().BeNull();

        monitor.Dispose();
    }

    [Fact]
    public async Task SweepAsync_ProgressTimeout_WithLifecycleManager_CallsFailRunAsync()
    {
        // Phase 1.6: stuck agent with lifecycle manager delegates to FailRunAsync
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null);

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(5) });

        var registry = new AgentRegistryService(_mockLogger.Object);
        var runService = new OrchestratorRunService(_mockLogger.Object);
        var dispatcher = new JobDispatcherService(registry, _mockLogger.Object);

        var monitor = new HeartbeatMonitorService(
            registry,
            runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            mockConfigStore.Object,
            _mockLogger.Object,
            lifecycleManager: mockLifecycle.Object);

        var entry = registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-stuck",
            Hostname = "host-stuck",
            Labels = new[] { "dotnet" }
        }, "conn-stuck");
        entry.ActiveJobId = "job-stuck";
        registry.TransitionStatus("agent-stuck", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-stuck",
            IssueIdentifier = "org/repo#200",
            IssueTitle = "Stuck",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-stuck",
            LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-10) // exceeds 5min timeout
        };
        runService.AddRun(run);

        // Act
        await monitor.SweepAsync(CancellationToken.None);

        // Assert: FailRunAsync called with reason containing "progress timeout"
        mockLifecycle.Verify(l => l.FailRunAsync("job-stuck",
            It.Is<string>(s => s.Contains("progress timeout")),
            It.IsAny<CancellationToken>()), Times.Once);

        monitor.Dispose();
    }

    [Fact]
    public async Task SweepAsync_OrphanedRun_WithLifecycleManager_CallsFailRunAsync()
    {
        // Phase 3: orphaned run with lifecycle manager
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null);

        var registry = new AgentRegistryService(_mockLogger.Object);
        var runService = new OrchestratorRunService(_mockLogger.Object);
        var dispatcher = new JobDispatcherService(registry, _mockLogger.Object);

        var monitor = new HeartbeatMonitorService(
            registry,
            runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            _mockConfigStore.Object,
            _mockLogger.Object,
            lifecycleManager: mockLifecycle.Object);

        var run = new PipelineRun
        {
            RunId = "orphan-lm",
            IssueIdentifier = "org/repo#300",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-gone" // not in registry
        };
        runService.AddRun(run);

        // Act
        await monitor.SweepAsync(CancellationToken.None);

        // Assert
        mockLifecycle.Verify(l => l.FailRunAsync("orphan-lm",
            "Agent deregistered (orphaned run)",
            It.IsAny<CancellationToken>()), Times.Once);

        monitor.Dispose();
    }

    [Fact]
    public async Task SweepAsync_OrphanRestored_WithLifecycleManager_CallsFailRunAsync()
    {
        // Phase 1.5: orphan not resumed with lifecycle manager
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(l => l.FailRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null);

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentDisconnectGracePeriod = TimeSpan.FromMinutes(5) });

        var registry = new AgentRegistryService(_mockLogger.Object);
        var runService = new OrchestratorRunService(_mockLogger.Object);
        var dispatcher = new JobDispatcherService(registry, _mockLogger.Object);

        var monitor = new HeartbeatMonitorService(
            registry,
            runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockLabelSwapper.Object,
            mockConfigStore.Object,
            _mockLogger.Object,
            lifecycleManager: mockLifecycle.Object);

        var entry = registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-orphan",
            Hostname = "host-orphan",
            Labels = new[] { "dotnet" }
        }, "conn-orphan");
        entry.ActiveJobId = "job-orphan";
        entry.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10); // past 5min grace
        registry.TransitionStatus("agent-orphan", AgentStatus.Busy);

        var run = new PipelineRun
        {
            RunId = "job-orphan",
            IssueIdentifier = "org/repo#400",
            IssueTitle = "Orphan resumed",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-orphan"
        };
        runService.AddRun(run);

        // Act
        await monitor.SweepAsync(CancellationToken.None);

        // Assert
        mockLifecycle.Verify(l => l.FailRunAsync("job-orphan",
            "Agent did not resume orphaned job within grace period",
            It.IsAny<CancellationToken>()), Times.Once);

        monitor.Dispose();
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
