using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="JobQueueDrainService"/>.
/// Tests the internal DrainAsync method directly.
/// </summary>
public class JobQueueDrainServiceTests
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly ConsolidationQueueService _consolidationQueue;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly Mock<IConsolidationDispatcher> _mockConsolidationDispatcher;
    private readonly JobQueueDrainService _service;

    public JobQueueDrainServiceTests()
    {
        var logger = new Mock<ILogger>().Object;
        _registry = new AgentRegistryService(logger);
        _dispatcher = new JobDispatcherService(_registry, logger);
        _mockJobDispatcher = new Mock<IJobDispatcher>();
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore
            .Setup(c => c.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);
        _consolidationQueue = new ConsolidationQueueService(logger);
        _mockConsolidationService = new Mock<IConsolidationService>();
        _mockConsolidationDispatcher = new Mock<IConsolidationDispatcher>();
        _service = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _consolidationQueue, _mockConsolidationService.Object, _mockConsolidationDispatcher.Object, new ShutdownSignal(), logger);
    }

    private AgentEntry RegisterIdleAgent(string agentId = "agent-1", IReadOnlyList<string>? labels = null)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host",
            Labels = labels ?? new[] { "kiro", "dotnet" }
        }, $"conn-{agentId}");
    }

    private PendingJob CreateJob(string issueId = "issue-1", IReadOnlyList<string>? labels = null) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderId = "ip",
        RepoProviderId = "rp",
        EnqueuedAt = DateTimeOffset.UtcNow,
        InitiatedBy = "test",
        RequiredLabels = labels ?? Array.Empty<string>()
    };

    [Fact]
    public async Task DrainAsync_EmptyQueue_DoesNothing()
    {
        RegisterIdleAgent();

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_NoIdleAgents_DoesNotDispatch()
    {
        _dispatcher.EnqueueJob(CreateJob());
        // No agents registered

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_QueuedJobAndIdleAgent_DispatchesDirectly()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-42"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-42"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-42"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_SuccessfulDispatch_MarkIssueCompleteCalledAfter()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-99"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        // Dedup entry should be removed after successful dispatch
        _dispatcher.IsIssueQueued("issue-99").Should().BeFalse();
    }

    [Fact]
    public async Task DrainAsync_DispatchFails_ReEnqueuesJobAndRetainsDedup()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-1"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued
        _dispatcher.QueueLength.Should().Be(1);
        // Dedup entry should remain active (not removed)
        _dispatcher.IsIssueQueued("issue-1").Should().BeTrue();
    }

    [Fact]
    public async Task DrainAsync_DispatchThrows_ReEnqueuesJobAndRetainsDedup()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-1"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued after exception
        _dispatcher.QueueLength.Should().Be(1);
        // Dedup entry should remain active
        _dispatcher.IsIssueQueued("issue-1").Should().BeTrue();
    }

    [Fact]
    public async Task DrainAsync_CancellationRequested_StopsEarly()
    {
        RegisterIdleAgent("agent-1");
        RegisterIdleAgent("agent-2");
        _dispatcher.EnqueueJob(CreateJob("issue-1"));
        _dispatcher.EnqueueJob(CreateJob("issue-2"));

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await _service.DrainAsync(cts.Token);

        // Should not dispatch anything since cancellation was requested
        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Signal_DoesNotThrow()
    {
        var act = () => _service.Signal();
        act.Should().NotThrow();
    }

    [Fact]
    public void Signal_MultipleCallsDoNotThrow()
    {
        // Signal is safe to call multiple times
        for (var i = 0; i < 100; i++)
            _service.Signal();
    }

    [Fact]
    public void DefaultDrainInterval_Is10Seconds()
    {
        JobQueueDrainService.DefaultDrainInterval.Should().Be(TimeSpan.FromSeconds(10));
    }

    #region Drain-Dispatch Dedup Continuity (Req 9.1, 9.3, 9.5)

    [Fact]
    public async Task DrainAsync_ConcurrentPollForSameIssue_RejectedWhileDrainInProgress()
    {
        // Scenario: Enqueue job → drain starts → concurrent poll for same issue → poll is rejected
        // Validates: Requirements 9.1, 9.5
        RegisterIdleAgent();
        var job = CreateJob("issue-concurrent");
        _dispatcher.EnqueueJob(job);

        // The issue is queued — IsIssueQueued should return true
        _dispatcher.IsIssueQueued("issue-concurrent").Should().BeTrue(
            "dedup entry must exist immediately after enqueue");

        // Set up dispatch to simulate in-flight dispatch (it will succeed)
        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentEntry _, PendingJob _, IReadOnlyList<string> _, CancellationToken _) =>
            {
                // During dispatch, verify a concurrent enqueue for the same issue is rejected
                var duplicateEnqueued = _dispatcher.EnqueueJob(new PendingJob
                {
                    IssueIdentifier = "issue-concurrent",
                    IssueProviderId = "ip",
                    RepoProviderId = "rp",
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    InitiatedBy = "concurrent-poll"
                });
                duplicateEnqueued.Should().BeFalse(
                    "dedup must remain active during drain→dispatch sequence (Req 9.5)");

                _dispatcher.IsIssueQueued("issue-concurrent").Should().BeTrue(
                    "IsIssueQueued must return true during in-flight dispatch");

                return true;
            });

        await _service.DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainAsync_SuccessfulDispatch_MarkIssueCompleteCalledAndRunServiceTracksIssue()
    {
        // Scenario: Enqueue job → drain dispatches successfully → MarkIssueComplete called → issue tracked by run service
        // Validates: Requirements 9.1, 9.3
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-tracked"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-tracked"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Before drain — dedup active
        _dispatcher.IsIssueQueued("issue-tracked").Should().BeTrue();

        await _service.DrainAsync(CancellationToken.None);

        // After successful dispatch — dedup entry removed (MarkIssueComplete called)
        _dispatcher.IsIssueQueued("issue-tracked").Should().BeFalse(
            "MarkIssueComplete must be called after successful dispatch (Req 9.3)");

        // Verify DispatchToAgentDirectAsync was called (which registers the run in run service)
        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-tracked"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Queue should be empty
        _dispatcher.QueueLength.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_DispatchFails_JobReEnqueuedAtBackAndDedupStillActive()
    {
        // Scenario: Enqueue job → drain fails dispatch → job re-enqueued at back of queue → dedup still active
        // Validates: Requirements 9.1, 9.3, 9.5
        RegisterIdleAgent();
        var firstJob = CreateJob("issue-fail-dedup");
        _dispatcher.EnqueueJob(firstJob);

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-fail-dedup"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);

        // After failed dispatch:
        // 1. Job must be re-enqueued
        _dispatcher.QueueLength.Should().Be(1, "job must be re-enqueued after failed dispatch");

        // 2. Dedup entry must still be active — prevents concurrent poll from enqueuing duplicate
        _dispatcher.IsIssueQueued("issue-fail-dedup").Should().BeTrue(
            "dedup entry must remain active after failed dispatch (Req 9.5)");

        // 3. Attempting to enqueue the same issue should be rejected
        var duplicateResult = _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-fail-dedup",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "retry-poll"
        });
        duplicateResult.Should().BeFalse(
            "concurrent poll for re-enqueued issue must be rejected while dedup is active");
    }

    #endregion

    #region Review/Decomposition routing (via DispatchToAgentDirectAsync)

    [Fact]
    public async Task DrainAsync_ReviewRunType_DispatchesDirectlyWithJob()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "pr-10",
            IssueTitle = "PR #10",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop",
            RequiredLabels = Array.Empty<string>(),
            RunType = PipelineRunType.Review,
            PrBranchName = "feature/x",
            PrDescription = "desc",
            PrUrl = "https://github.com/org/repo/pull/10",
            PrTargetBranch = "main"
        });

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(),
                It.Is<PendingJob>(j => j.IssueIdentifier == "pr-10" && j.RunType == PipelineRunType.Review),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(),
                It.Is<PendingJob>(j => j.IssueIdentifier == "pr-10" && j.RunType == PipelineRunType.Review),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_DecompositionRunType_DispatchesDirectlyWithJob()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "epic-5",
            IssueTitle = "Epic #5",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop",
            RequiredLabels = Array.Empty<string>(),
            RunType = PipelineRunType.DecompositionAnalysis
        });

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(),
                It.Is<PendingJob>(j => j.IssueIdentifier == "epic-5" && j.RunType == PipelineRunType.DecompositionAnalysis),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(),
                It.Is<PendingJob>(j => j.IssueIdentifier == "epic-5" && j.RunType == PipelineRunType.DecompositionAnalysis),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Consolidation drain

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchesAndTransitionsToRunning()
    {
        RegisterIdleAgent();
        _consolidationQueue.EnqueueJob(new PendingConsolidationJob
        {
            RunId = "crun-1",
            Type = ConsolidationRunType.BrainConsolidation,
            WorkspacePath = "/tmp/ws",
            RequiredLabels = Array.Empty<string>(),
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync("crun-1", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockConsolidationDispatcher.Verify(
            d => d.TryDispatchToAgentAsync("crun-1", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockConsolidationService.Verify(
            s => s.TransitionToRunningAsync("crun-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJobCancelled_SkipsDispatch()
    {
        RegisterIdleAgent();
        _consolidationQueue.EnqueueJob(new PendingConsolidationJob
        {
            RunId = "crun-cancel",
            Type = ConsolidationRunType.RefactoringDetection,
            WorkspacePath = "/tmp/ws",
            RequiredLabels = Array.Empty<string>(),
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        // Cancel the run before drain
        _consolidationQueue.CancelRun("crun-cancel");

        // Re-enqueue since CancelRun removes from queue — simulate the race where
        // the job was dequeued but cancel happened between dequeue and dispatch.
        // Actually, CancelRun removes from queue, so we need a different approach:
        // Enqueue a new job and cancel it via IsRunCancelled check
        _consolidationQueue.EnqueueJob(new PendingConsolidationJob
        {
            RunId = "crun-cancel2",
            Type = ConsolidationRunType.RefactoringDetection,
            WorkspacePath = "/tmp/ws2",
            RequiredLabels = Array.Empty<string>(),
            EnqueuedAt = DateTimeOffset.UtcNow
        });
        _consolidationQueue.CancelRun("crun-cancel2");

        await _service.DrainAsync(CancellationToken.None);

        // Dispatch should never be called for cancelled runs
        _mockConsolidationDispatcher.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationDispatchFails_ExceedsMaxRetries_MarksAsFailed()
    {
        RegisterIdleAgent();

        // Create a job that's already at MaxRetryCount - 1
        var job = new PendingConsolidationJob
        {
            RunId = "crun-fail",
            Type = ConsolidationRunType.HarnessSuggestions,
            WorkspacePath = "/tmp/ws",
            RequiredLabels = Array.Empty<string>(),
            EnqueuedAt = DateTimeOffset.UtcNow,
            RetryCount = ConsolidationQueueService.MaxRetryCount - 1
        };
        _consolidationQueue.EnqueueJob(job);

        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);

        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync("crun-fail", ConsolidationRunStatus.Failed, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationDispatchThrows_ReEnqueuesOrFails()
    {
        RegisterIdleAgent();
        _consolidationQueue.EnqueueJob(new PendingConsolidationJob
        {
            RunId = "crun-throw",
            Type = ConsolidationRunType.BrainConsolidation,
            WorkspacePath = "/tmp/ws",
            RequiredLabels = Array.Empty<string>(),
            EnqueuedAt = DateTimeOffset.UtcNow,
            RetryCount = 0
        });

        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent error"));

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued (retry count < max)
        _consolidationQueue.QueueLength.Should().Be(1);
    }

    #endregion

}
