using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
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
    private readonly Mock<IConsolidationRunStore> _mockRunStore;
    private readonly Mock<ILogger> _mockLogger;
    private readonly JobQueueDrainService _service;
    private readonly JobQueueDrainService _serviceWithRunStore;

    public JobQueueDrainServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        var logger = _mockLogger.Object;
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
        _mockRunStore = new Mock<IConsolidationRunStore>();
        _service = new JobQueueDrainService(new JobQueueDrainDependencies(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger));
        _serviceWithRunStore = new JobQueueDrainService(new JobQueueDrainDependencies(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, _mockRunStore.Object));
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
        // Signal() wakes the drain loop — calling it with no queued jobs should be safe
        _dispatcher.EnqueueJob(CreateJob("issue-signal-test"));
        var queueLengthBefore = _dispatcher.QueueLength;
        var act = () => _service.Signal();
        act.Should().NotThrow();
        // Signal() must not alter the queue itself — draining is async and not triggered here
        _dispatcher.QueueLength.Should().Be(queueLengthBefore, "Signal() must not synchronously drain the queue");
    }

    [Fact]
    public void Signal_MultipleCallsDoNotThrow()
    {
        // Signal is safe to call multiple times
        for (var i = 0; i < 100; i++)
            _service.Signal();

        // If we got here without exception, the test passed
        Assert.True(true, "Signal() must not throw on repeated calls");
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

        // After drain, dedup entry released (MarkIssueComplete was called on success)
        _dispatcher.IsIssueQueued("issue-concurrent").Should().BeFalse(
            "dedup entry must be released after successful dispatch (Req 9.3)");
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

    // TODO: [WARNING] All DrainAsync_Consolidation* tests use RepoProviderId = "consolidation" (same as
    // IssueProviderId) because ProviderConfigId rejects empty strings. This means no test exercises the
    // runtime path where a consolidation PendingJob has a different or mismatched RepoProviderId. If the
    // drain service logic branches on RepoProviderId for consolidation jobs (e.g., config lookup), those
    // branches are untested. Consider adding a test variant where IssueProviderId != RepoProviderId.

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchesViaTryDispatchToAgentAsync()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-1",
            IssueProviderId = "consolidation",
            RepoProviderId = "consolidation",
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
            RepoProviderId = "consolidation",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.HarnessSuggestions,
            ConsolidationTemplateId = "t-1",
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued
        _dispatcher.QueueLength.Should().Be(1);
        // Attempt counter should be incremented
        _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_CancelledRun_Discards()
    {
        var runStore = new Mock<IConsolidationRunStore>();
        runStore.Setup(s => s.GetByIdAsync("crun-cancelled", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = "crun-cancelled", Status = ConsolidationRunStatus.Cancelled, Type = ConsolidationRunType.RefactoringDetection, StartedAtUtc = DateTime.UtcNow });

        var logger = new Mock<ILogger>().Object;
        var serviceWithStore = new JobQueueDrainService(new JobQueueDrainDependencies(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, runStore.Object));

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-cancelled",
            IssueProviderId = "consolidation",
            RepoProviderId = "consolidation",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        await serviceWithStore.DrainAsync(CancellationToken.None);

        // Dispatch should never be called for cancelled runs
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()),
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
            RepoProviderId = "consolidation",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent error"));

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued (exception handled)
        _dispatcher.QueueLength.Should().Be(1);
        // Attempt counter should be incremented
        _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(1);
    }

    #endregion

    #region Consolidation retry limit (Issue #1691)

    [Fact]
    public async Task DrainAsync_ConsolidationJob_ExhaustedRetries_TransitionsToFailed()
    {
        var run = new ConsolidationRun
        {
            RunId = "crun-exhaust",
            Status = ConsolidationRunStatus.Queued,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTime.UtcNow
        };
        _mockRunStore.Reset();
        _mockRunStore
            .Setup(s => s.GetByIdAsync("crun-exhaust", It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        _mockRunStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConsolidationDispatchService.Reset();
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        RegisterIdleAgent();

        for (var cycle = 1; cycle <= 5; cycle++)
        {
            if (cycle == 1)
            {
                _dispatcher.EnqueueJob(new PendingJob
                {
                    IssueIdentifier = "crun-exhaust",
                    IssueProviderId = "consolidation",
                    RepoProviderId = "consolidation",
                    InitiatedBy = "consolidation",
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    RequiredLabels = Array.Empty<string>(),
                    ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
                    ConsolidationWorkspacePath = "/tmp/ws"
                });
            }

            await _serviceWithRunStore.DrainAsync(CancellationToken.None);

            if (cycle < 5)
            {
                _dispatcher.QueueLength.Should().Be(1, "cycle {0}: job should be re-enqueued", cycle);
                _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(cycle,
                    "cycle {0}: attempt count should be {0}", cycle);
            }
            else
            {
                _dispatcher.QueueLength.Should().Be(0, "job should be discarded after 5 failures");
                _dispatcher.IsIssueQueued("crun-exhaust").Should().BeFalse(
                    "dedup entry should be removed after 5 failures");
            }
        }

        _mockRunStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r => r.RunId == "crun-exhaust" && r.Status == ConsolidationRunStatus.Failed),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_ExhaustedRetriesViaException_TransitionsToFailed()
    {
        var run = new ConsolidationRun
        {
            RunId = "crun-exhaust-ex",
            Status = ConsolidationRunStatus.Queued,
            Type = ConsolidationRunType.RefactoringDetection,
            StartedAtUtc = DateTime.UtcNow
        };
        _mockRunStore.Reset();
        _mockRunStore
            .Setup(s => s.GetByIdAsync("crun-exhaust-ex", It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        _mockRunStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConsolidationDispatchService.Reset();
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent unavailable"));

        RegisterIdleAgent();

        for (var cycle = 1; cycle <= 5; cycle++)
        {
            if (cycle == 1)
            {
                _dispatcher.EnqueueJob(new PendingJob
                {
                    IssueIdentifier = "crun-exhaust-ex",
                    IssueProviderId = "consolidation",
                    RepoProviderId = "consolidation",
                    InitiatedBy = "consolidation",
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    RequiredLabels = Array.Empty<string>(),
                    ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
                    ConsolidationWorkspacePath = "/tmp/ws"
                });
            }

            await _serviceWithRunStore.DrainAsync(CancellationToken.None);

            if (cycle < 5)
            {
                _dispatcher.QueueLength.Should().Be(1, "cycle {0}: job should be re-enqueued", cycle);
                _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(cycle,
                    "cycle {0}: exception path should also increment attempt count", cycle);
            }
            else
            {
                _dispatcher.QueueLength.Should().Be(0, "job should be discarded after 5 exceptions");
                _dispatcher.IsIssueQueued("crun-exhaust-ex").Should().BeFalse();
            }
        }

        _mockRunStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r => r.RunId == "crun-exhaust-ex" && r.Status == ConsolidationRunStatus.Failed),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_RetryCountPreservedAcrossCycles()
    {
        _mockConsolidationDispatchService.Reset();
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-counter",
            IssueProviderId = "consolidation",
            RepoProviderId = "consolidation",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        await _service.DrainAsync(CancellationToken.None);
        _dispatcher.QueueLength.Should().Be(1);
        _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(1);

        await _service.DrainAsync(CancellationToken.None);
        _dispatcher.QueueLength.Should().Be(1);
        _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(2);

        await _service.DrainAsync(CancellationToken.None);
        _dispatcher.QueueLength.Should().Be(1);
        _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(3);

        await _service.DrainAsync(CancellationToken.None);
        _dispatcher.QueueLength.Should().Be(1);
        _dispatcher.GetQueuedJobs()[0].ConsolidationDispatchAttempt.Should().Be(4);
    }

    [Fact]
    public async Task DrainAsync_NonConsolidationJob_ExceptionDoesNotTriggerRetryLimit()
    {
        _mockJobDispatcher.Reset();
        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-no-limit"));

        for (var i = 0; i < 7; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, "pipeline job should not be discarded (cycle {0})", i + 1);
            _dispatcher.IsIssueQueued("issue-no-limit").Should().BeTrue(
                "pipeline job dedup should remain active (cycle {0})", i + 1);
        }
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_ExhaustedRetries_StoreNull_StillDiscardsJob()
    {
        _mockConsolidationDispatchService.Reset();
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        RegisterIdleAgent();

        for (var cycle = 1; cycle <= 5; cycle++)
        {
            if (cycle == 1)
            {
                _dispatcher.EnqueueJob(new PendingJob
                {
                    IssueIdentifier = "crun-no-store",
                    IssueProviderId = "consolidation",
                    RepoProviderId = "consolidation",
                    InitiatedBy = "consolidation",
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    RequiredLabels = Array.Empty<string>(),
                    ConsolidationRunType = ConsolidationRunType.HarnessSuggestions,
                    ConsolidationWorkspacePath = "/tmp/ws"
                });
            }

            await _service.DrainAsync(CancellationToken.None);

            if (cycle < 5)
            {
                _dispatcher.QueueLength.Should().Be(1, "cycle {0}: job should be re-enqueued", cycle);
            }
            else
            {
                _dispatcher.QueueLength.Should().Be(0, "job should be discarded even without run store");
                _dispatcher.IsIssueQueued("crun-no-store").Should().BeFalse(
                    "dedup entry should be removed even without run store");
            }
        }

        _mockLogger.Verify(
            l => l.Warning(
                "Drain: cannot update consolidation run {RunId} to {Status} — no IConsolidationRunStore available",
                It.IsAny<string>(),
                It.IsAny<ConsolidationRunStatus>()),
            Times.AtLeastOnce);
    }

    #endregion
}
