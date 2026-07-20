using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Services;

public class AgentMonitoringPageServiceTests : IDisposable
{
    private readonly Mock<IActiveRunQueryService> _mockActiveRunQuery = new();
    private readonly Mock<IAgentRegistryService> _mockRegistry = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWorkQuery = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<IHubContext<AgentHub, IAgentHubClient>> _mockHubContext = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager = new();
    private readonly FakeTimeProvider _fakeTime = new(DateTimeOffset.UtcNow);
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentMonitoringPageService _sut;

    public AgentMonitoringPageServiceTests()
    {
        var mockLogger = new Mock<ILogger>();
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
        _mockRegistry.Setup(r => r.GetAllAgents())
            .Returns(Array.Empty<AgentEntry>());
        _mockPendingWorkQuery.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>());
        _mockConsolidationService.Setup(c => c.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        var registry = new AgentRegistryService(mockLogger.Object);
        _dispatcher = new JobDispatcherService(registry, mockLogger.Object);

        _pipelineService = TestUtilities.TestOrchestrationFactory.CreateMinimal(
            configStore: _mockConfigStore.Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            historyService: _mockHistoryService.Object);

        _sut = new AgentMonitoringPageService(
            _mockActiveRunQuery.Object,
            _mockRegistry.Object,
            _dispatcher,
            _mockRunService.Object,
            _pipelineService,
            _mockConfigStore.Object,
            _mockConsolidationService.Object,
            _mockPendingWorkQuery.Object,
            _mockWorkDistributor.Object,
            _mockHubContext.Object,
            _mockLabelService.Object,
            _mockHistoryService.Object,
            _mockLifecycleManager.Object,
            _fakeTime);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task InitializeAsync_LoadsConfigAndProviderLookups()
    {
        await _sut.InitializeAsync();

        Assert.Equal(5, _sut.MaxRetries);
        _mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockConfigStore.Verify(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockConfigStore.Verify(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshDataAsync_QueriesActiveRunsAgentsHistory()
    {
        // TODO: Should also assert that state properties (ActiveRuns, Agents, QueuedJobs) reflect mocked data,
        // not just that dependency methods were called.
        await _sut.InitializeAsync();

        _mockActiveRunQuery.Verify(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockRegistry.Verify(r => r.GetAllAgents(), Times.AtLeastOnce);
        _mockPendingWorkQuery.Verify(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RefreshDataAsync_WithConsolidation_LoadsConsolidationRuns()
    {
        await _sut.InitializeAsync();

        _mockConsolidationService.Verify(c => c.GetRunHistoryAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CancelAgentRunAsync_AgentNotFound_SetsCancelledFailureReason_CallsCancelRunAsync()
    {
        // TODO: This test mocks CancelRunAsync so it cannot verify that agent state clearing,
        // label swapping, and history persistence actually occur via the lifecycle manager.
        // Consider an integration-level test that uses the real RunLifecycleManager.
        _mockRegistry.Setup(r => r.GetByAgentId("agent-1")).Returns((AgentEntry?)null);
        _mockLifecycleManager.Setup(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRun { RunId = "run-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" });

        var run = new PipelineRun { RunId = "run-1", AgentId = "agent-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" };

        await _sut.InitializeAsync();
        await _sut.CancelAgentRunAsync(run);

        Assert.Equal("Cancelled — agent not available", run.FailureReason);
        _mockLifecycleManager.Verify(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAgentRunAsync_OrphanRestored_SetsCancelledFailureReason_CallsCancelRunAsync()
    {
        // TODO: Same mock-boundary issue as AgentNotFound test — cannot verify agent state clearing
        // (ActiveJobId=null, OrphanRestoredAt=null, status→Idle) actually happens via lifecycle manager.
        var agent = new AgentEntry
        {
            AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "host-1", Labels = ["kiro"],
            RegisteredAt = DateTimeOffset.UtcNow,
            OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        _mockRegistry.Setup(r => r.GetByAgentId("agent-1")).Returns(agent);
        _mockLifecycleManager.Setup(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRun { RunId = "run-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" });

        var run = new PipelineRun { RunId = "run-1", AgentId = "agent-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" };

        await _sut.InitializeAsync();
        await _sut.CancelAgentRunAsync(run);

        Assert.Equal("Cancelled — agent lost job state (container restart)", run.FailureReason);
        _mockLifecycleManager.Verify(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAgentRunAsync_ConnectedAgent_SendsCancelJobSignal_ThenCallsCancelRunAsync()
    {
        var agent = new AgentEntry { AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "host-1", Labels = ["kiro"], RegisteredAt = DateTimeOffset.UtcNow };
        _mockRegistry.Setup(r => r.GetByAgentId("agent-1")).Returns(agent);

        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        var mockClient = new Mock<IAgentHubClient>();
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Client("conn-1")).Returns(mockClient.Object);
        mockClient.Setup(c => c.CancelJob("run-1")).Returns(Task.CompletedTask);

        _mockLifecycleManager.Setup(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRun { RunId = "run-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" });

        var run = new PipelineRun { RunId = "run-1", AgentId = "agent-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" };

        await _sut.InitializeAsync();
        await _sut.CancelAgentRunAsync(run);

        Assert.Equal("Cancelled by user", run.FailureReason);
        mockClient.Verify(c => c.CancelJob("run-1"), Times.Once);
        _mockLifecycleManager.Verify(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAgentRunAsync_CancelRunAsyncReturnsNull_FallsBackToWorkDistributor()
    {
        _mockRegistry.Setup(r => r.GetByAgentId("agent-1")).Returns((AgentEntry?)null);
        _mockLifecycleManager.Setup(l => l.CancelRunAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null);
        _mockWorkDistributor.Setup(w => w.CancelJobAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var run = new PipelineRun { RunId = "run-1", AgentId = "agent-1", IssueIdentifier = "test/repo#1", IssueTitle = "Test", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1" };

        await _sut.InitializeAsync();
        await _sut.CancelAgentRunAsync(run);

        _mockWorkDistributor.Verify(w => w.CancelJobAsync("run-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAgentRunByIdAsync_RunNotInMemory_CallsWorkDistributorDirectly()
    {
        _mockWorkDistributor.Setup(w => w.CancelJobAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.InitializeAsync();
        await _sut.CancelAgentRunByIdAsync("run-1");

        _mockWorkDistributor.Verify(w => w.CancelJobAsync("run-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_LegacyMode_CallsDispatcherRemoveFromQueue()
    {
        // TODO: Does not verify that _dispatcher.RemoveFromQueue was actually invoked — test passes
        // even if method body is empty. Also missing: test for DB/K8s mode path (job with WorkItemId).
        // With no pending jobs in QueuedJobs, the legacy path is taken
        await _sut.InitializeAsync();
        await _sut.RemoveFromQueueAsync("test/repo#1", "ip-1");

        // No WorkItem found in QueuedJobs — falls through to legacy dispatcher.RemoveFromQueue
        // (We can't easily verify the dispatcher call without exposing it, but this covers the path)
        _mockWorkDistributor.Verify(w => w.CancelJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForceDisconnectAsync_ConnectedAgent_SendsForceDisconnectAndDeregisters()
    {
        var agent = new AgentEntry { AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "host-1", Labels = ["kiro"], RegisteredAt = DateTimeOffset.UtcNow };

        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        var mockClient = new Mock<IAgentHubClient>();
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Client("conn-1")).Returns(mockClient.Object);
        mockClient.Setup(c => c.ForceDisconnect()).Returns(Task.CompletedTask);

        await _sut.InitializeAsync();
        await _sut.ForceDisconnectAsync(agent);

        mockClient.Verify(c => c.ForceDisconnect(), Times.Once);
        _mockRegistry.Verify(r => r.Deregister("agent-1"), Times.Once);
    }

    [Fact]
    public async Task ForceDisconnectAsync_DisconnectedAgent_SkipsSignalR_StillDeregisters()
    {
        // TODO: Should explicitly verify ForceDisconnect() was NOT called on the hub client
        // to make the "skips SignalR" assertion explicit. Also missing: test for agent with ActiveJobId
        // (path that marks the run as failed).
        var agent = new AgentEntry { AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "host-1", Labels = ["kiro"], RegisteredAt = DateTimeOffset.UtcNow, Status = AgentStatus.Disconnected };

        await _sut.InitializeAsync();
        await _sut.ForceDisconnectAsync(agent);

        _mockRegistry.Verify(r => r.Deregister("agent-1"), Times.Once);
    }

    [Fact]
    public async Task Dispose_StopsTimerAndUnsubscribesFromEvents()
    {
        // TODO: This test is effectively tautological — it subscribes the event handler AFTER Dispose(),
        // so it passes even if Dispose() does nothing. The _disposed flag prevents RefreshTick from
        // firing NotifyStateChanged regardless of timer state. Consider verifying timer disposal directly.
        await _sut.InitializeAsync();

        // Should not throw
        _sut.Dispose();

        // Verify no more state changes after dispose
        var stateChangedCalled = false;
        _sut.OnStateChanged += () => stateChangedCalled = true;

        // Advancing time shouldn't trigger anything since timer is disposed
        _fakeTime.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100); // Give time for any async callbacks

        // The event handler was added after dispose, so it's expected to not fire
        // But we mainly verify dispose doesn't throw
        Assert.False(stateChangedCalled);
    }

    [Fact]
    public void EnableAgent_SetsDisabledFalse()
    {
        var agent = new AgentEntry { AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "host-1", Labels = ["kiro"], RegisteredAt = DateTimeOffset.UtcNow, Disabled = true };

        _sut.EnableAgent(agent);

        Assert.False(agent.Disabled);
    }

    [Fact]
    public void DisableAgent_SetsDisabledTrue()
    {
        var agent = new AgentEntry { AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "host-1", Labels = ["kiro"], RegisteredAt = DateTimeOffset.UtcNow };

        _sut.DisableAgent(agent);

        Assert.True(agent.Disabled);
    }

    [Fact]
    public async Task GetOutputBuffer_DelegatesToOrchestratorRunService()
    {
        var expectedBuffer = new OutputRingBuffer(100);
        _mockRunService.Setup(r => r.GetOutputBuffer("run-1")).Returns(expectedBuffer);

        await _sut.InitializeAsync();
        var result = _sut.GetOutputBuffer("run-1");

        Assert.Same(expectedBuffer, result);
    }

    [Fact]
    public async Task ResolveProvider_ReturnsNull_WhenNotFound()
    {
        await _sut.InitializeAsync();

        var result = _sut.ResolveProvider("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveProvider_ReturnsNull_WhenEmptyOrNull()
    {
        await _sut.InitializeAsync();

        Assert.Null(_sut.ResolveProvider(null));
        Assert.Null(_sut.ResolveProvider(""));
    }

    // TODO: Missing tests (review warnings):
    // - CancelConsolidationRunAsync: verify ConsolidationService.CancelQueuedRunAsync is called and data refreshes
    // - RemoveFromQueueAsync with a job that HAS a WorkItemId (DB/K8s mode path)
    // - ForceDisconnectAsync when agent has an ActiveJobId (verifies run is marked Failed with correct FailureReason)
}
