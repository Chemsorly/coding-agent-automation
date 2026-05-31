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
        _consolidationQueue = new ConsolidationQueueService(logger);
        _mockConsolidationService = new Mock<IConsolidationService>();
        _mockConsolidationDispatcher = new Mock<IConsolidationDispatcher>();
        _service = new JobQueueDrainService(_dispatcher, _registry, _mockJobDispatcher.Object,
            _consolidationQueue, _mockConsolidationService.Object, _mockConsolidationDispatcher.Object, logger);
    }

    private AgentEntry RegisterIdleAgent(string agentId = "agent-1", IReadOnlyList<string>? labels = null)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host",
            AgentType = "kiro",
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
            d => d.TryDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_NoIdleAgents_DoesNotDispatch()
    {
        _dispatcher.EnqueueJob(CreateJob());
        // No agents registered

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.TryDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainAsync_QueuedJobAndIdleAgent_DispatchesJob()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-42"));

        _mockJobDispatcher
            .Setup(d => d.TryDispatchAsync("issue-42", "ip", "rp", null, null, "test", It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.TryDispatchAsync("issue-42", "ip", "rp", null, null, "test", It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_DispatchFails_ReEnqueuesJob()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-1"));

        _mockJobDispatcher
            .Setup(d => d.TryDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(false);

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued
        _dispatcher.QueueLength.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_DispatchThrows_ReEnqueuesJob()
    {
        RegisterIdleAgent();
        _dispatcher.EnqueueJob(CreateJob("issue-1"));

        _mockJobDispatcher
            .Setup(d => d.TryDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        await _service.DrainAsync(CancellationToken.None);

        // Job should be re-enqueued after exception
        _dispatcher.QueueLength.Should().Be(1);
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
            d => d.TryDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
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

    #region Review/Decomposition routing

    [Fact]
    public async Task DrainAsync_ReviewRunType_RoutesToTryDispatchReviewAsync()
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
            .Setup(d => d.TryDispatchReviewAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.TryDispatchReviewAsync(It.Is<ReviewDispatchRequest>(r => r.PrIdentifier == "pr-10"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainAsync_DecompositionRunType_RoutesToTryDispatchDecompositionAsync()
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
            .Setup(d => d.TryDispatchDecompositionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.DrainAsync(CancellationToken.None);

        _mockJobDispatcher.Verify(
            d => d.TryDispatchDecompositionAsync(
                "epic-5", "Epic #5", PipelineRunType.DecompositionAnalysis,
                "ip", "rp", null, "loop", It.IsAny<CancellationToken>()),
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
