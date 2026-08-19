using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using AwesomeAssertions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentMonitoringPageService"/> — validates cancellation orchestration,
/// data refresh, and state management.
/// <para>
/// Spec 044: IOrchestratorRunService, IRunLifecycleManager, and IHubContext removed from the service.
/// Cancel operations now route through IWorkDistributor only.
/// </para>
/// </summary>
public sealed class AgentMonitoringPageServiceTests
{
    private static readonly string[] s_KiroLabels = new[] { "kiro" };

    private readonly Mock<IActiveRunQueryService> _mockActiveRunQuery = new();
    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWorkQuery = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly AgentMonitoringPageService _sut;

    public AgentMonitoringPageServiceTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
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

        _sut = new AgentMonitoringPageService(new AgentMonitoringPageServiceDependencies(
            _mockActiveRunQuery.Object,
            _registry,
            _dispatcher,
            _mockConfigStore.Object,
            _mockConsolidationService.Object,
            _mockPendingWorkQuery.Object,
            _mockWorkDistributor.Object,
            _mockHistoryService.Object));
    }

    private static PipelineRun CreateRun(string runId, string agentId = "agent-1")
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
            Labels = s_KiroLabels
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
            new PendingJob { IssueIdentifier = "c1", IssueProviderId = "ip-1", RepoProviderId = "rp-1", EnqueuedAt = DateTimeOffset.UtcNow, InitiatedBy = "test", RunType = PipelineRunType.Consolidation, ConsolidationRunType = ConsolidationRunType.BrainConsolidation }
        };
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);

        await _sut.RefreshDataAsync();

        _sut.QueuedJobs.Should().HaveCount(1);
        _sut.QueuedJobs[0].IssueIdentifier.Value.Should().Be("1");
    }

    // ── CancelAgentRunAsync — Spec 044 degraded mode ──

    [Fact]
    public async Task CancelAgentRunAsync_RoutesToWorkDistributor()
    {
        var run = CreateRun("run-1", agentId: "agent-1");

        _mockWorkDistributor
            .Setup(w => w.CancelJobAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.CancelAgentRunAsync(run);

        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync("run-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── CancelAgentRunByIdAsync ──

    [Fact]
    public async Task CancelAgentRunByIdAsync_CallsWorkDistributor()
    {
        _mockWorkDistributor
            .Setup(w => w.CancelJobAsync("run-4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.CancelAgentRunByIdAsync("run-4");

        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync("run-4", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelAgentRunByIdAsync_WhenNotFound_LogsAndRefreshes()
    {
        // WorkDistributor returns false (not found) — should not throw
        _mockWorkDistributor
            .Setup(w => w.CancelJobAsync("run-not-in-memory", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.CancelAgentRunByIdAsync("run-not-in-memory");

        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync("run-not-in-memory", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── RemoveFromQueueAsync ──

    [Fact]
    public async Task RemoveFromQueueAsync_DbMode_CallsWorkDistributorCancelJob()
    {
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
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>());
        await _sut.RefreshDataAsync();

        await _sut.RemoveFromQueueAsync("org/repo#99", "ip-1");

        // No WorkDistributor call when no matching queued job
        _mockWorkDistributor.Verify(
            w => w.CancelJobAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── ForceDisconnectAsync — Spec 044 degraded mode ──

    [Fact]
    public async Task ForceDisconnectAsync_DeregistersAgent_WithoutHubCall()
    {
        var agent = RegisterAgent("agent-force", "conn-force");
        _registry.TransitionStatus("agent-force", AgentStatus.Busy);

        await _sut.ForceDisconnectAsync(agent);

        // Agent deregistered from local registry
        _registry.GetByAgentId("agent-force").Should().BeNull();
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

    // ── RefreshConsolidationAsync ──

    [Fact]
    public async Task RefreshConsolidationAsync_WhenServiceReturnsNull_SetsEmptyCollections()
    {
        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ConsolidationRun>)null!);

        await _sut.RefreshConsolidationAsync();

        _sut.ActiveConsolidationRuns.Should().BeEmpty();
        _sut.QueuedConsolidationRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshConsolidationAsync_WhenServiceReturnsData_FiltersCorrectly()
    {
        var runs = new List<ConsolidationRun>
        {
            new() { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow, Status = ConsolidationRunStatus.Running },
            new() { RunId = "r2", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow, Status = ConsolidationRunStatus.Queued },
            new() { RunId = "r3", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow, Status = ConsolidationRunStatus.Succeeded },
            new() { RunId = "r4", Type = ConsolidationRunType.RefactoringDetection, StartedAtUtc = DateTimeOffset.UtcNow, Status = ConsolidationRunStatus.Running },
        };
        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(runs);

        await _sut.RefreshConsolidationAsync();

        _sut.ActiveConsolidationRuns.Should().HaveCount(2);
        _sut.ActiveConsolidationRuns.Select(r => r.RunId).Should().BeEquivalentTo(["r1", "r4"]);
        _sut.QueuedConsolidationRuns.Should().HaveCount(1);
        _sut.QueuedConsolidationRuns[0].RunId.Should().Be("r2");
    }
}
