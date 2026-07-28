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
        _service = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger);
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

        // Job should be re-enqueued
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

        // Job should be re-enqueued (exception handled)
        _dispatcher.QueueLength.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchFails5Times_TransitionsToFailed()
    {
        var runStore = new Mock<IConsolidationRunStore>();
        runStore.Setup(s => s.GetByIdAsync("crun-retry-exhaust", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "crun-retry-exhaust",
                Status = ConsolidationRunStatus.Queued,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTime.UtcNow
            });

        var logger = new Mock<ILogger>().Object;
        var serviceWithStore = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, runStore.Object);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-retry-exhaust",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync("crun-retry-exhaust", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 4 times — job should be re-enqueued each time
        for (var i = 0; i < 4; i++)
        {
            await serviceWithStore.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"job must be re-enqueued after attempt {i + 1}");
            _dispatcher.IsIssueQueued("crun-retry-exhaust").Should().BeTrue($"dedup must be retained after attempt {i + 1}");
        }

        // 5th drain — job should transition to Failed, NOT re-enqueued
        await serviceWithStore.DrainAsync(CancellationToken.None);

        _dispatcher.QueueLength.Should().Be(0, "job must NOT be re-enqueued after max retries exhausted");
        _dispatcher.IsIssueQueued("crun-retry-exhaust").Should().BeFalse("dedup must be released after max retries exhausted");

        runStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r =>
                    r.Status == ConsolidationRunStatus.Failed &&
                    r.Summary!.Contains("Max dispatch retries exhausted")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify dispatch was attempted 5 times
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync("crun-retry-exhaust", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchThrows5Times_TransitionsToFailed()
    {
        var runStore = new Mock<IConsolidationRunStore>();
        runStore.Setup(s => s.GetByIdAsync("crun-throw-exhaust", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "crun-throw-exhaust",
                Status = ConsolidationRunStatus.Queued,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTime.UtcNow
            });

        var logger = new Mock<ILogger>().Object;
        var serviceWithStore = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, runStore.Object);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-throw-exhaust",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync("crun-throw-exhaust", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent error"));

        // Drain 4 times — job should be re-enqueued each time (exception handled)
        for (var i = 0; i < 4; i++)
        {
            await serviceWithStore.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"job must be re-enqueued after exception attempt {i + 1}");
            _dispatcher.IsIssueQueued("crun-throw-exhaust").Should().BeTrue($"dedup must be retained after exception attempt {i + 1}");
        }

        // 5th drain — job should transition to Failed
        await serviceWithStore.DrainAsync(CancellationToken.None);

        _dispatcher.QueueLength.Should().Be(0, "job must NOT be re-enqueued after max retries exhausted via exception path");
        _dispatcher.IsIssueQueued("crun-throw-exhaust").Should().BeFalse("dedup must be released after max retries exhausted via exception path");

        runStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r =>
                    r.Status == ConsolidationRunStatus.Failed &&
                    r.Summary!.Contains("Max dispatch retries exhausted")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_DispatchFailsLessThan5Times_ContinuesReEnqueuing()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-boundary",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.HarnessSuggestions,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 4 times — job should always be re-enqueued (no failure transition)
        for (var i = 0; i < 4; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"job must be re-enqueued after attempt {i + 1}");
            _dispatcher.IsIssueQueued("crun-boundary").Should().BeTrue($"dedup must be retained after attempt {i + 1}");
        }

        _dispatcher.QueueLength.Should().Be(1);
        _dispatcher.IsIssueQueued("crun-boundary").Should().BeTrue();
    }

    [Fact]
    public async Task DrainAsync_NonConsolidationJob_DispatchFails5Times_StillReEnqueued()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-no-limit",
            IssueTitle = "Normal issue",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "loop",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            TaskType = WorkItemTaskType.Implementation
        });

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-no-limit"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 5+ times — non-consolidation job should ALWAYS be re-enqueued, never failed
        for (var i = 0; i < 6; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"non-consolidation job must be re-enqueued after attempt {i + 1}");
            _dispatcher.IsIssueQueued("issue-no-limit").Should().BeTrue($"dedup must be retained for non-consolidation job after attempt {i + 1}");
        }
    }

    [Fact]
    public async Task DrainAsync_NonConsolidationJob_DispatchThrows6Times_StillReEnqueued()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-no-limit-ex",
            IssueTitle = "Normal issue",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "loop",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            TaskType = WorkItemTaskType.Implementation
        });

        _mockJobDispatcher
            .Setup(d => d.DispatchToAgentDirectAsync(
                It.IsAny<AgentEntry>(), It.Is<PendingJob>(j => j.IssueIdentifier == "issue-no-limit-ex"),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        // Drain 6 times — non-consolidation job throwing should ALWAYS be re-enqueued, never failed
        for (var i = 0; i < 6; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"non-consolidation job must be re-enqueued after exception attempt {i + 1}");
            _dispatcher.IsIssueQueued("issue-no-limit-ex").Should().BeTrue($"dedup must be retained for non-consolidation job after exception attempt {i + 1}");
        }
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_AlreadyFailedInStore_DiscardsWithoutDispatch()
    {
        var runStore = new Mock<IConsolidationRunStore>();
        runStore.Setup(s => s.GetByIdAsync("crun-already-failed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "crun-already-failed",
                Status = ConsolidationRunStatus.Failed,
                Type = ConsolidationRunType.RefactoringDetection,
                StartedAtUtc = DateTime.UtcNow
            });

        var logger = new Mock<ILogger>().Object;
        var serviceWithStore = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, runStore.Object);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-already-failed",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        await serviceWithStore.DrainAsync(CancellationToken.None);

        // Dispatch should never be called for already-failed runs
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Queue should be empty (not re-enqueued)
        _dispatcher.QueueLength.Should().Be(0);
        _dispatcher.IsIssueQueued("crun-already-failed").Should().BeFalse();
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_RetryCounterPersistsAcrossDrainCycles()
    {
        var runStore = new Mock<IConsolidationRunStore>();
        runStore.Setup(s => s.GetByIdAsync("crun-persist", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "crun-persist",
                Status = ConsolidationRunStatus.Queued,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTime.UtcNow
            });

        var logger = new Mock<ILogger>().Object;
        var serviceWithStore = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _mockConfigStore.Object, _mockConsolidationDispatchService.Object, new ShutdownSignal(), logger, runStore.Object);

        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-persist",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync("crun-persist", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 2 times — counter should be preserved across re-enqueues
        for (var i = 0; i < 2; i++)
        {
            await serviceWithStore.DrainAsync(CancellationToken.None);
            _dispatcher.QueueLength.Should().Be(1, $"job must be re-enqueued after attempt {i + 1}");
        }

        // Drain 3 more times — retries 3, 4, 5, counter persists across cycles
        for (var i = 0; i < 3; i++)
        {
            await serviceWithStore.DrainAsync(CancellationToken.None);
        }

        // After 5 total failures, job should be Failed
        _dispatcher.QueueLength.Should().Be(0);
        _dispatcher.IsIssueQueued("crun-persist").Should().BeFalse();

        runStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r => r.Status == ConsolidationRunStatus.Failed),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_ConsolidationJob_NullRunStore_RemovesDedupOnMaxRetries()
    {
        // Default service has null IConsolidationRunStore
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "crun-nullstore",
            IssueProviderId = "consolidation",
            RepoProviderId = "",
            InitiatedBy = "consolidation",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RequiredLabels = Array.Empty<string>(),
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationWorkspacePath = "/tmp/ws"
        });

        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync("crun-nullstore", ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Drain 5 times
        for (var i = 0; i < 5; i++)
        {
            await _service.DrainAsync(CancellationToken.None);
        }

        // Even without a run store, dedup must be released and job not re-enqueued
        _dispatcher.QueueLength.Should().Be(0, "job must not be re-enqueued after max retries even without run store");
        _dispatcher.IsIssueQueued("crun-nullstore").Should().BeFalse("dedup must be released after max retries even without run store");
    }

    #endregion
}
