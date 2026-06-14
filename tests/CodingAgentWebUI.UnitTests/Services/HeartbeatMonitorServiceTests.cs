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

    [Fact]
    public async Task SweepAsync_BusyAgent_ProgressTimeout_NoRun_NoException()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-missing";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // No run added for this job — simulates race condition
        await _monitor.SweepAsync(CancellationToken.None);

        // Agent stays busy (no crash, no action)
        _registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Busy);
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

    private AgentEntry RegisterAgent(string agentId, string connectionId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            AgentType = "kiro-dotnet",
            Labels = new[] { "dotnet" }
        }, connectionId);
    }

    public void Dispose()
    {
        _monitor.Dispose();
    }
}
