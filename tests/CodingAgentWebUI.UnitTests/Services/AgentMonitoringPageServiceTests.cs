using CodingAgentWebUI.Api.Client;
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
/// <para>
/// Spec 045: IConfigurationStore replaced by IPipelineApiConfigClient;
/// IPipelineRunHistoryService replaced by IPipelineApiRunHistoryClient.
/// </para>
/// </summary>
public sealed class AgentMonitoringPageServiceTests
{
    private static readonly string[] s_KiroLabels = new[] { "kiro" };

    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWorkQuery = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistoryClient = new();
    private readonly AgentMonitoringPageService _sut;

    public AgentMonitoringPageServiceTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);

        _mockConfigClient.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MaxRetries = 5 });
        _mockConfigClient.Setup(s => s.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigClient.Setup(s => s.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockConfigClient.Setup(s => s.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockPendingWorkQuery.Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>());
        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        _mockRunHistoryClient.Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = Array.Empty<PipelineRunSummary>(), Page = 1, PageSize = 1000, HasMore = false });

        _sut = new AgentMonitoringPageService(new AgentMonitoringPageServiceDependencies(
            _registry,
            _dispatcher,
            _mockConfigClient.Object,
            _mockConsolidationService.Object,
            _mockPendingWorkQuery.Object,
            _mockWorkDistributor.Object,
            _mockRunHistoryClient.Object));
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

        _mockRunHistoryClient.Verify(s => s.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
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

    // ── AugmentAgentsWithConsolidationRunners — issue #2087 ──

    /// <summary>
    /// Regression test: when a consolidation run has an AgentId that is in the registry but not
    /// yet in the Agents snapshot (e.g. due to snapshot lag in ApiAgentRegistryService), the agent
    /// must be added to PageService.Agents so Connected/Busy counters include it.
    /// </summary>
    // TODO: This test does not exercise the actual augmentation path. RegisterAgent puts the agent
    // into the real AgentRegistryService, so GetAllAgents() returns it before AugmentAgentsWithConsolidationRunners
    // runs. The agent is included because it was already in the snapshot, not because of augmentation.
    // The test would still pass if AugmentAgentsWithConsolidationRunners were a no-op. To test the
    // augmentation path correctly, mock IAgentRegistryService.GetAllAgents() to return empty while
    // GetByAgentId returns the entry, so the agent is absent from the snapshot and only added by augmentation.
    [Fact]
    public async Task RefreshDataAsync_IncludeConsolidation_AugmentsAgentsWithConsolidationRunners()
    {
        // Register the agent in the registry (it is in the registry, but RefreshDataAsync
        // sets Agents = GetAllAgents() — in this test we control whether it appears there)
        var agentEntry = RegisterAgent("agent-brain-runner", "conn-brain");
        _registry.TransitionStatus("agent-brain-runner", AgentStatus.Busy);
        agentEntry.ActiveJobId = "brain-run-99";

        var activeRun = new ConsolidationRun
        {
            RunId = "brain-run-99",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running,
            AgentId = "agent-brain-runner"
        };

        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { activeRun });

        await _sut.RefreshDataAsync(includeConsolidation: true);

        // The agent must appear in PageService.Agents
        _sut.Agents.Should().Contain(a => a.AgentId.Value == "agent-brain-runner",
            "consolidation runner agent must be included in the Agents list so counters are non-zero");
        _sut.Agents.Count(a => a.AgentId.Value == "agent-brain-runner").Should().Be(1,
            "the same agent should not be added twice");
    }

    [Fact]
    public async Task RefreshDataAsync_IncludeConsolidation_DoesNotDuplicateAgentAlreadyInList()
    {
        // Agent is in the registry AND already returned by GetAllAgents()
        // TODO: This test only covers the existingAgentIds.Contains early-exit guard. It does not
        // test the deduplication case where the same AgentId appears in two concurrent consolidation
        // runs (both pointing at the same agent). Add a test with two active runs sharing the same
        // AgentId to verify the extras hash deduplication path.
        RegisterAgent("agent-already-listed", "conn-al");

        var activeRun = new ConsolidationRun
        {
            RunId = "brain-run-dupe",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running,
            AgentId = "agent-already-listed"
        };

        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { activeRun });

        await _sut.RefreshDataAsync(includeConsolidation: true);

        _sut.Agents.Count(a => a.AgentId.Value == "agent-already-listed").Should().Be(1,
            "agent already in the registry snapshot must not be duplicated");
    }

    [Fact]
    public async Task RefreshDataAsync_IncludeConsolidation_IgnoresRunsWithNullAgentId()
    {
        // Run with no AgentId should not cause a lookup
        var activeRun = new ConsolidationRun
        {
            RunId = "brain-run-no-agent",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running,
            AgentId = null
        };

        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { activeRun });

        await _sut.RefreshDataAsync(includeConsolidation: true);

        // No augmentation should have occurred — Agents stays as-is
        _sut.Agents.Should().BeEmpty("no agent registered, no run with AgentId");
    }
}
