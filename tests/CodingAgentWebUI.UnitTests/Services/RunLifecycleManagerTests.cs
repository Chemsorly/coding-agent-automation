using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RunLifecycleManager"/> — validates lifecycle coordination
/// across run service, agent registry, label swapper, history, and work item transitions.
/// </summary>
public sealed class RunLifecycleManagerTests
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly JobDispatcherService _dispatcher;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _sut = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelSwapper.Object,
            _dispatcher,
            _mockLogger.Object,
            workItemTransition: null); // Legacy mode — no DB
    }

    // ── AgentAcceptedRunAsync ────────────────────────────────────────────

    [Fact]
    public async Task AgentAcceptedRunAsync_ReviewRunType_SwapsLabelWithRepoProviderAndPullRequestTarget()
    {
        // Arrange
        var run = CreateRun("run-1", PipelineRunType.Review);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-1", "agent-1", "org/repo#42",
            "issue-provider-1", "repo-provider-1", PipelineRunType.Review, CancellationToken.None);

        // Assert: label swap uses repoProviderConfigId + PullRequest target
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "repo-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_ImplementationRunType_SwapsLabelWithIssueProviderAndIssueTarget()
    {
        // Arrange
        var run = CreateRun("run-2", PipelineRunType.Implementation);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-2", "agent-1", "org/repo#10",
            "issue-provider-1", "repo-provider-1", PipelineRunType.Implementation, CancellationToken.None);

        // Assert: label swap uses issueProviderConfigId + Issue target
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "issue-provider-1", "org/repo#10", AgentLabels.InProgress, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_DecompositionAnalysisRunType_SwapsLabelWithIssueProviderAndIssueTarget()
    {
        // Arrange
        var run = CreateRun("run-3", PipelineRunType.DecompositionAnalysis);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-3", "agent-1", "org/repo#5",
            "issue-provider-1", "repo-provider-1", PipelineRunType.DecompositionAnalysis, CancellationToken.None);

        // Assert: label swap uses issueProviderConfigId + Issue target
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "issue-provider-1", "org/repo#5", AgentLabels.InProgress, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_SetsAgentIdOnRun_AndTransitionsAgentToBusy()
    {
        // Arrange
        var run = CreateRun("run-4", PipelineRunType.Implementation);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-4", "agent-1", "org/repo#1",
            "ip-1", "rp-1", PipelineRunType.Implementation, CancellationToken.None);

        // Assert
        run.AgentId.Should().Be("agent-1");
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().Be("run-4");
        agent.Status.Should().Be(AgentStatus.Busy);
    }

    // ── FailRunAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task FailRunAsync_RemovesRun_PersistsHistory_ClearsAgent_SwapsLabel()
    {
        // Arrange
        var run = CreateRun("run-fail", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);

        var entry = RegisterAgent("agent-1");
        entry.ActiveJobId = "run-fail";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // Act
        var result = await _sut.FailRunAsync("run-fail", "Something went wrong", CancellationToken.None);

        // Assert: run returned
        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-fail");
        result.FailureReason.Should().Be("Something went wrong");
        result.CurrentStep.Should().Be(PipelineStep.Failed);

        // Run removed from active
        _runService.GetRun("run-fail").Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-fail"), It.IsAny<CancellationToken>()), Times.Once);

        // Agent cleared and transitioned to Idle
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);

        // Label swapped to error via issue provider (Implementation → Issue target)
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRunAsync_RunDoesNotExist_ReturnsNull()
    {
        // Act: no run was added with this ID
        var result = await _sut.FailRunAsync("non-existent-run", "reason", CancellationToken.None);

        // Assert
        result.Should().BeNull();

        // No side effects
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailRunAsync_ReviewRun_SwapsLabelViaRepoProvider()
    {
        // Arrange
        var run = CreateRun("run-review-fail", PipelineRunType.Review);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        var result = await _sut.FailRunAsync("run-review-fail", "Review failed", CancellationToken.None);

        // Assert: label swap routes via repo provider for Review runs
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "rp-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CompleteRunAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CompleteRunAsync_RemovesRun_PersistsHistory_MarksIssueComplete()
    {
        // Arrange
        var run = CreateRun("run-complete", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);

        // Act
        var result = await _sut.CompleteRunAsync("run-complete", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-complete");

        // Run removed
        _runService.GetRun("run-complete").Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-complete"), It.IsAny<CancellationToken>()), Times.Once);

        // CompleteRunAsync does NOT clear agent state or swap labels (caller does that)
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteRunAsync_RunDoesNotExist_ReturnsNull()
    {
        var result = await _sut.CompleteRunAsync("ghost", WorkItemStatus.Succeeded, CancellationToken.None);
        result.Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineRun CreateRun(string runId, PipelineRunType runType)
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = runType
        };
    }

    private AgentEntry RegisterAgent(string agentId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = new[] { "dotnet" }
        }, $"conn-{agentId}");
    }
}

/// <summary>
/// Validates finding 1B-001: FailRunAsync must still clean up dedup tracker
/// even if AddRunToHistoryAsync throws, preventing stale entries.
/// </summary>
public sealed class RunLifecycleManagerResilienceTests
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly JobDispatcherService _dispatcher;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerResilienceTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _sut = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelSwapper.Object,
            _dispatcher,
            _mockLogger.Object,
            workItemTransition: null);
    }

    [Fact]
    public async Task FailRunAsync_WhenHistoryThrows_StillClearsDedupTracker()
    {
        // Arrange: set up a run that's "in-progress" in the dedup tracker
        var run = CreateRun("run-fail-history-err");
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#1",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = new[] { "dotnet" },
            InitiatedBy = "test"
        });
        // Dequeue to simulate "in processing" state
        var entry = RegisterAgent("agent-1");
        _dispatcher.DequeueForAgent(entry);

        // Make history throw
        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failed"));

        // Act
        var result = await _sut.FailRunAsync("run-fail-history-err", "test failure", CancellationToken.None);

        // Assert: run was still returned (claimed successfully)
        result.Should().NotBeNull();

        // Dedup tracker was cleared despite the history exception
        _dispatcher.IsIssueQueued("org/repo#1", "ip-1").Should().BeFalse();

        // Agent state was cleared
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task CompleteRunAsync_WhenHistoryThrows_StillClearsDedupTracker()
    {
        // Arrange
        var run = CreateRun("run-complete-err");
        _runService.AddRun(run);
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#1",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = new[] { "dotnet" },
            InitiatedBy = "test"
        });
        var entry = RegisterAgent("agent-1");
        _dispatcher.DequeueForAgent(entry);

        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failed"));

        // Act
        var result = await _sut.CompleteRunAsync("run-complete-err", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: still returned the run
        result.Should().NotBeNull();

        // Dedup tracker was cleared
        _dispatcher.IsIssueQueued("org/repo#1", "ip-1").Should().BeFalse();
    }

    private static PipelineRun CreateRun(string runId)
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation
        };
    }

    private AgentEntry RegisterAgent(string agentId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = new[] { "dotnet" }
        }, $"conn-{agentId}");
    }
}
