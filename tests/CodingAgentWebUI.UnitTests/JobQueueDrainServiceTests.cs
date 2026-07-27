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
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IConsolidationDispatchService> _mockConsolidationDispatchService;
    private readonly Mock<IConsolidationRunStore> _mockConsolidationRunStore;
    private readonly JobQueueDrainService _service;

    public JobQueueDrainServiceTests()
    {
        var logger = new Mock<ILogger>().Object;
        _registry = new AgentRegistryService(logger);
        _dispatcher = new JobDeduplicationGuardService(_registry, logger);
        _mockJobDispatcher = new Mock<IJobDispatcher>();
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore
            .Setup(c => c.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);
        _mockConsolidationDispatchService = new Mock<IConsolidationDispatchService>();
        _mockConsolidationRunStore = new Mock<IConsolidationRunStore>();
        _mockConsolidationRunStore
            .Setup(s => s.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string runId, CancellationToken _) => new ConsolidationRun
            {
                RunId = runId,
                Status = ConsolidationRunStatus.Queued,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTime.UtcNow
            });
        _service = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger,
            _mockConsolidationRunStore.Object);
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

    private PendingJob CreateConsolidationJob(string runId = "crun-1", ConsolidationRunType? type = null) => new()
    {
        IssueIdentifier = runId,
        IssueProviderId = "consolidation",
        RepoProviderId = "",
        InitiatedBy = "consolidation",
        EnqueuedAt = DateTimeOffset.UtcNow,
        RequiredLabels = Array.Empty<string>(),
        ConsolidationRunType = type ?? ConsolidationRunType.BrainConsolidation,
        ConsolidationWorkspacePath = "/tmp/ws"
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

    #region Consolidation drain (Legacy mode via PendingJob.IsConsolidation)

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchesViaTryDispatchToAgentAsync()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-1",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationTemplateId = null,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync("crun-1", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync("crun-1", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchFails_ReEnqueues()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-fail",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.HarnessSuggestions,
            ConsolidationTemplateId = "t-1",
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued (RetryCount=1 < MaxConsolidationRetries=5)
        _dispatcher.QueueLength.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_CancelledRun_Discards()
    {
        var runStore = new Mock<IConsolidationRunStore>();
        runStore.Setup(s => s.GetByIdAsync("crun-cancelled", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = "crun-cancelled", Status = ConsolidationRunStatus.Cancelled, Type = ConsolidationRunType.RefactoringDetection, StartedAtUtc = DateTime.UtcNow });

        var logger = new Mock<ILogger>().Object;
        var serviceWithStore = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, runStore.Object);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-cancelled",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        await serviceWithStore.DrainAsync(CancellationToken.None);

        // Dispatch should never be called for cancelled runs
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Queue should be empty (not re-enqueued)
        _dispatcher.QueueLength.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchThrows_ReEnqueues()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-throw",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent error"));

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued (RetryCount=1 < MaxConsolidationRetries=5)
        _dispatcher.QueueLength.Should().Be(1);
    }

    #endregion

    #region Consolidation retry limit (#1691)

    [Fact]
    public async Task DrainAsync_ConsolidationJob_FailsLessThanMaxRetries_ReEnqueuesEachTime()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateConsolidationJob("crun-retry"));

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 4 times — each should re-enqueue (4 < MaxConsolidationRetries=5)
        for (var i = 0; i < 4; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"job should still be in queue after attempt {i + 1}");
        }

        // Verify RetryCount was incremented on each attempt
        var queuedJobs = _dispatcher.GetQueuedJobs();
        queuedJobs.Should().HaveCount(1);
        queuedJobs[0].RetryCount.Should().Be(4);

        // Verify SaveRunAsync was never called (never exceeded limit)
        _mockConsolidationRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_FailsMaxRetries_TransitionsToFailed()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateConsolidationJob("crun-exhaust"));

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 5 times — on the 5th, should transition to Failed instead of re-enqueueing
        for (var i = 0; i < JobQueueDrainService.MaxConsolidationRetries; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
        }

        // Queue should be empty (job NOT re-enqueued)
        _dispatcher.QueueLength.Should().Be(0);

        // SaveRunAsync should have been called with a Failed run
        _mockConsolidationRunStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r =>
                    r.RunId == "crun-exhaust" &&
                    r.Status == ConsolidationRunStatus.Failed &&
                    r.Summary != null &&
                    r.Summary.Contains("Max dispatch retries exhausted")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchThrowsAndExceedsMaxRetries_TransitionsToFailed()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateConsolidationJob("crun-throw-exhaust"));

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent error"));

        // Drain 5 times — on the 5th exception, should transition to Failed
        for (var i = 0; i < JobQueueDrainService.MaxConsolidationRetries; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
        }

        // Queue should be empty
        _dispatcher.QueueLength.Should().Be(0);

        // SaveRunAsync should have been called with a Failed run
        _mockConsolidationRunStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r =>
                    r.RunId == "crun-throw-exhaust" &&
                    r.Status == ConsolidationRunStatus.Failed &&
                    r.Summary != null &&
                    r.Summary.Contains("Max dispatch retries exhausted")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_NonConsolidationJob_DispatchFails_StillReEnqueuesAfterManyFailures()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-no-limit"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 10 times (more than MaxConsolidationRetries=5) — non-consolidation jobs should still re-enqueue
        for (var i = 0; i < 10; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"non-consolidation job should still be in queue after attempt {i + 1}");
        }

        // SaveRunAsync should never be called (retry limit only applies to consolidation jobs)
        _mockConsolidationRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_NonConsolidationJob_DispatchThrows_StillReEnqueuedAfterManyFailures()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-throw-no-limit"));

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        // Drain 10 times — non-consolidation jobs should still re-enqueue on exception
        for (var i = 0; i < 10; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"non-consolidation job should still be in queue after exception attempt {i + 1}");
        }

        // SaveRunAsync should never be called
        _mockConsolidationRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_RetryCountPersistsAcrossDrainCycles()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateConsolidationJob("crun-persist"));

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);
        await _service.DrainAsync(CancellationToken.None);

        // After 2 drains, the job in the queue should have RetryCount==2
        var queuedJobs = _dispatcher.GetQueuedJobs();
        queuedJobs.Should().HaveCount(1);
        queuedJobs[0].RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_FailsMaxRetries_WhenRunStoreIsNull_StillStopsReenqueuing()
    {
        var logger = new Mock<ILogger>().Object;
        var serviceWithoutStore = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger,
            consolidationRunStore: null);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateConsolidationJob("crun-nullstore"));

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        for (var i = 0; i < JobQueueDrainService.MaxConsolidationRetries; i++)
        {
            await serviceWithoutStore.DrainAsync(CancellationToken.None);
        }

        // Queue should be empty (dedup released even without store)
        _dispatcher.QueueLength.Should().Be(0);

        // Dedup entry should be released
        _dispatcher.IsIssueQueued("crun-nullstore").Should().BeFalse();
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_FailsMaxRetries_WhenRunAlreadyFailed_StillReleasesDedup()
    {
        // Setup: mock returns a run already in Failed status — FailConsolidationAsync should
        // skip the store write but the dedup is still released (MarkIssueComplete called first).
        _mockConsolidationRunStore
            .Setup(s => s.GetByIdAsync("crun-already-failed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "crun-already-failed",
                Status = ConsolidationRunStatus.Failed,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTime.UtcNow
            });

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateConsolidationJob("crun-already-failed"));

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        for (var i = 0; i < JobQueueDrainService.MaxConsolidationRetries; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
        }

        // Queue should be empty (dedup released)
        _dispatcher.QueueLength.Should().Be(0);

        // SaveRunAsync should NOT be called (run already in terminal state)
        _mockConsolidationRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion
}