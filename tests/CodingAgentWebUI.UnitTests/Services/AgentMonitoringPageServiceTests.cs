using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentMonitoringPageService"/> — validates cancellation orchestration,
/// data refresh, and state management extracted from the AgentMonitoring Razor component.
/// </summary>
/// <remarks>
/// TODO: These tests mock IRunLifecycleManager, so they cannot detect if the lifecycle manager's
/// contract changes (e.g., stops calling LabelService or ClearAgentState). The integration between
/// page service and lifecycle manager is only covered by separate RunLifecycleManagerTests.
/// Consider adding integration tests that use a real RunLifecycleManager to detect contract drift.
/// </remarks>
public sealed class AgentMonitoringPageServiceTests
{
    private readonly Mock<IActiveRunQueryService> _mockActiveRunQuery = new();
    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWorkQuery = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<IHubContext<AgentHub, IAgentHubClient>> _mockHubContext = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager = new();
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly AgentMonitoringPageService _sut;

    public AgentMonitoringPageServiceTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MaxRetries = 5 });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActiveRunSummary>());
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>());
        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockConfigStore.Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            runService: _runService,
            historyService: _mockHistoryService.Object);

        _sut = new AgentMonitoringPageService(
            _mockActiveRunQuery.Object,
            _registry,
            _dispatcher,
            _runService,
            _pipelineService,
            _mockConfigStore.Object,
            _mockConsolidationService.Object,
            _mockPendingWorkQuery.Object,
            _mockWorkDistributor.Object,
            _mockHubContext.Object,
            _mockLabelService.Object,
            _mockHistoryService.Object,
            _mockLifecycleManager.Object);
    }

    private PipelineRun CreateRun(string runId, string agentId = "agent-1")
    {
        return new PipelineRun
        {
            RunId = runId,
            AgentId = agentId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test Issue",
            CurrentStep = PipelineStep.GeneratingCode,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };
    }

    private AgentEntry RegisterAgent(string agentId, string connectionId = "conn-1")
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "test-host",
            Labels = new[] { "kiro" }
        }, connectionId);
    }

    // ── InitializeAsync ──

    [Fact]
    public async Task InitializeAsync_LoadsMaxRetriesFromConfig()
    {
        await _sut.InitializeAsync();

        _sut.MaxRetries.Should().Be(5);
    }

    [Fact]
    public async Task InitializeAsync_LoadsDataAndRefreshes()
    {
        await _sut.InitializeAsync();

        _mockActiveRunQuery.Verify(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockPendingWorkQuery.Verify(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── RefreshDataAsync ──

    [Fact]
    public async Task RefreshDataAsync_FiltersConsolidationJobs()
    {
        var jobs = new[]
        {
            new PendingJob { IssueIdentifier = "1", IssueProviderId = "ip-1", RepoProviderId = "rp-1", EnqueuedAt = DateTimeOffset.UtcNow, InitiatedBy = "test" },
            new PendingJob { IssueIdentifier = "c1", IssueProviderId = "ip-1", RepoProviderId = "rp-1", EnqueuedAt = DateTimeOffset.UtcNow, InitiatedBy = "test", ConsolidationRunType = ConsolidationRunType.BrainConsolidation }
        };
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);

        await _sut.RefreshDataAsync();

        _sut.QueuedJobs.Should().HaveCount(1);
        _sut.QueuedJobs[0].IssueIdentifier.Should().Be("1");
    }

    // ── CancelAgentRunAsync — Agent not found ──

    // TODO: This test only verifies the mock interaction (CancelRunAsync called with correct reason).
    // It does not assert observable state changes (e.g., that RefreshDataAsync was triggered or that
    // the run is removed from ActiveRuns). Consider adding state assertions for robustness.
    [Fact]
    public async Task CancelAgentRunAsync_AgentNotFound_DelegatesToLifecycleManagerWithReason()
    {
        var run = CreateRun("run-1", agentId: "nonexistent-agent");
        _runService.AddRun(run);

        _mockLifecycleManager
            .Setup(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>(), "Cancelled — agent not available"))
            .ReturnsAsync((PipelineRun?)null);

        await _sut.CancelAgentRunAsync(run);

        _mockLifecycleManager.Verify(
            l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>(), "Cancelled — agent not available"),
            Times.Once);
    }

    // ── CancelAgentRunAsync — Orphan-restored ──

    // TODO: This test does not assert post-conditions on agent state (e.g., that OrphanRestoredAt is
    // cleared after cancellation via ClearAgentState in the lifecycle manager). Consider adding
    // assertions to detect regressions where agent cleanup is skipped.
    [Fact]
    public async Task CancelAgentRunAsync_OrphanRestored_DelegatesToLifecycleManagerWithReason()
    {
        var run = CreateRun("run-2", agentId: "agent-1");
        _runService.AddRun(run);

        var agent = RegisterAgent("agent-1", "conn-1");
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        _mockLifecycleManager
            .Setup(l => l.CancelRunAsync("run-2", It.IsAny<CancellationToken>(), "Cancelled — agent lost job state (container restart)"))
            .ReturnsAsync((PipelineRun?)null);

        await _sut.CancelAgentRunAsync(run);

        _mockLifecycleManager.Verify(
            l => l.CancelRunAsync("run-2", It.IsAny<CancellationToken>(), "Cancelled — agent lost job state (container restart)"),
            Times.Once);
    }

    // ── CancelAgentRunAsync — Connected agent ──

    [Fact]
    public async Task CancelAgentRunAsync_ConnectedAgent_SendsCancelJobAndDelegatesToLifecycleManager()
    {
        var run = CreateRun("run-3", agentId: "agent-1");
        _runService.AddRun(run);

        var agent = RegisterAgent("agent-1", "conn-1");

        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        var mockClient = new Mock<IAgentHubClient>();
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Client("conn-1")).Returns(mockClient.Object);
        mockClient.Setup(c => c.CancelJob("run-3")).Returns(Task.CompletedTask);

        _mockLifecycleManager
            .Setup(l => l.CancelRunAsync("run-3", It.IsAny<CancellationToken>(), "Cancelled by user"))
            .ReturnsAsync((PipelineRun?)null);

        await _sut.CancelAgentRunAsync(run);

        // SignalR CancelJob sent
        mockClient.Verify(c => c.CancelJob("run-3"), Times.Once);

        // Lifecycle manager called with reason
        _mockLifecycleManager.Verify(
            l => l.CancelRunAsync("run-3", It.IsAny<CancellationToken>(), "Cancelled by user"),
            Times.Once);
    }

    // ── CancelAgentRunByIdAsync ──

    // TODO: This test uses It.IsAny<string?>() for failureReason verification, which is weaker than
    // the other cancel tests that verify exact reason strings. Consider asserting the exact reason
    // ("Cancelled — agent not available") to detect unintended reason changes.
    [Fact]
    public async Task CancelAgentRunByIdAsync_RunInMemory_DelegatesToCancelAgentRunAsync()
    {
        var run = CreateRun("run-4", agentId: "agent-1");
        _runService.AddRun(run);
        // Agent not found → delegates to lifecycle manager via "agent not available" path
        _mockLifecycleManager
            .Setup(l => l.CancelRunAsync("run-4", It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((PipelineRun?)null);

        await _sut.CancelAgentRunByIdAsync("run-4");

        _mockLifecycleManager.Verify(
            l => l.CancelRunAsync("run-4", It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelAgentRunByIdAsync_RunNotInMemory_FallsBackToWorkDistributor()
    {
        // No run in memory
        _mockWorkDistributor
            .Setup(w => w.CancelJobAsync("run-not-in-memory", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.CancelAgentRunByIdAsync("run-not-in-memory");

        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync("run-not-in-memory", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── RemoveFromQueueAsync ──

    [Fact]
    public async Task RemoveFromQueueAsync_DbMode_CallsWorkDistributorCancelJob()
    {
        // Seed queued jobs state
        var jobs = new[]
        {
            new PendingJob { IssueIdentifier = "org/repo#5", IssueProviderId = "ip-1", RepoProviderId = "rp-1", EnqueuedAt = DateTimeOffset.UtcNow, InitiatedBy = "test", WorkItemId = "wi-5" }
        };
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);
        await _sut.RefreshDataAsync();

        _mockWorkDistributor
            .Setup(w => w.CancelJobAsync("wi-5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.RemoveFromQueueAsync("org/repo#5", "ip-1");

        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync("wi-5", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_LegacyMode_CallsDispatcherRemoveFromQueue()
    {
        // No matching job in queue (WorkItemId = null simulated by empty queue)
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>());
        await _sut.RefreshDataAsync();

        // Enqueue via the dispatcher so RemoveFromQueue has something to remove
        // (The dispatcher tracks queued items internally for legacy mode)
        // Just verify the method doesn't throw — no mock needed since it calls the real dispatcher
        await _sut.RemoveFromQueueAsync("org/repo#99", "ip-1");

        // No WorkDistributor call in legacy mode
        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── ForceDisconnectAsync ──

    [Fact]
    public async Task ForceDisconnectAsync_SendsForceDisconnect_MarksRunFailed_DeregistersAgent()
    {
        var agent = RegisterAgent("agent-force", "conn-force");
        agent.ActiveJobId = "run-force";
        _registry.TransitionStatus("agent-force", AgentStatus.Busy);

        var run = CreateRun("run-force", agentId: "agent-force");
        _runService.AddRun(run);

        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        var mockClient = new Mock<IAgentHubClient>();
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Client("conn-force")).Returns(mockClient.Object);
        mockClient.Setup(c => c.ForceDisconnect()).Returns(Task.CompletedTask);

        await _sut.ForceDisconnectAsync(agent);

        // ForceDisconnect signal sent
        mockClient.Verify(c => c.ForceDisconnect(), Times.Once);

        // Run marked as failed (inline lifecycle — no LifecycleManager call)
        run.FailureReason.Should().Be("Force disconnected by operator");
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.CompletedAtOffset.Should().NotBeNull();

        // Agent deregistered
        _registry.GetByAgentId("agent-force").Should().BeNull();

        // Lifecycle manager NOT called (current behavior — inline bypass)
        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never);
    }

    // ── Resolvers ──

    [Fact]
    public async Task ResolveProvider_ReturnsNull_WhenConfigIdIsNullOrEmpty()
    {
        await _sut.InitializeAsync();

        _sut.ResolveProvider(null).Should().BeNull();
        _sut.ResolveProvider("").Should().BeNull();
    }

    [Fact]
    public async Task ResolveProfileName_ReturnsFallback_WhenProfileNotFound()
    {
        await _sut.InitializeAsync();

        var result = _sut.ResolveProfileName("some-long-profile-id");
        result.Should().Contain("(deleted)");
    }
}
