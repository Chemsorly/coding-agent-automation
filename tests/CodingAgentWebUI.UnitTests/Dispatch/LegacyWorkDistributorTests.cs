using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="LegacyWorkDistributor"/>.
/// Verifies facade delegation to underlying services.
/// </summary>
public class LegacyWorkDistributorTests
{
    private readonly Mock<IJobDispatcher> _mockJobDispatcher = new();
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();
    private readonly JobDispatcherService _dispatcherService;
    private readonly LegacyWorkDistributor _sut;

    public LegacyWorkDistributorTests()
    {
        var logger = Mock.Of<ILogger>();
        var registry = new CodingAgentWebUI.Orchestration.Registry.AgentRegistryService(logger);
        _dispatcherService = new JobDispatcherService(registry, logger);

        _sut = new LegacyWorkDistributor(
            _mockJobDispatcher.Object,
            _dispatcherService,
            _mockRunService.Object,
            logger);
    }

    // ── DistributeAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_Implementation_DelegatesToTryDispatchAsync()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                "org/repo#1", "ip-1", "rp-1", null, null, "loop",
                It.IsAny<CancellationToken>(), "Fix bug", null))
            .ReturnsAsync(true);

        var request = CreateRequest(WorkItemTaskType.Implementation);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_WhenNoAgent_ReturnsFailureWithMessage()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<PipelineProject?>()))
            .ReturnsAsync(false);

        var request = CreateRequest(WorkItemTaskType.Implementation);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.WorkItemId.Should().BeNull();
        result.ErrorMessage.Should().Be("No agent available");
    }

    [Fact]
    public async Task DistributeAsync_Review_DelegatesToTryDispatchReviewAsync()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchReviewAsync(
                It.IsAny<ReviewDispatchRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<PipelineProject?>()))
            .ReturnsAsync(true);

        var request = CreateRequest(WorkItemTaskType.Review);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockJobDispatcher.Verify(d => d.TryDispatchReviewAsync(
            It.Is<ReviewDispatchRequest>(r =>
                r.PrIdentifier == "org/repo#1" &&
                r.IssueProviderId == "ip-1" &&
                r.RepoProviderId == "rp-1"),
            It.IsAny<CancellationToken>(),
            It.IsAny<PipelineProject?>()), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_Decomposition_DelegatesToTryDispatchDecompositionAsync()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchDecompositionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<PipelineProject?>()))
            .ReturnsAsync(true);

        var request = CreateRequest(WorkItemTaskType.Decomposition);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockJobDispatcher.Verify(d => d.TryDispatchDecompositionAsync(
            "org/repo#1", "Fix bug", PipelineRunType.Implementation,
            "ip-1", "rp-1", null, "loop",
            It.IsAny<CancellationToken>(),
            null, It.IsAny<PipelineProject?>()), Times.Once);
    }

    // ── CancelJobAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_AlwaysReturnsFalse()
    {
        var result = await _sut.CancelJobAsync("some-id", CancellationToken.None);
        result.Should().BeFalse();
    }

    // ── GetJobStatusAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetJobStatusAsync_AlwaysReturnsUnknown()
    {
        var result = await _sut.GetJobStatusAsync("any-id", CancellationToken.None);
        result.Should().Be(JobDistributionStatus.Unknown);
    }

    // ── IsIssueDistributedAsync ─────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_DelegatesToIsIssueBeingProcessedOrQueued()
    {
        _mockJobDispatcher.Setup(d => d.IsIssueBeingProcessedOrQueued("org/repo#1", "ip-1"))
            .Returns(true);

        var result = await _sut.IsIssueDistributedAsync("org/repo#1", "ip-1", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_WhenNotActive_ReturnsFalse()
    {
        _mockJobDispatcher.Setup(d => d.IsIssueBeingProcessedOrQueued("org/repo#1", "ip-1"))
            .Returns(false);

        var result = await _sut.IsIssueDistributedAsync("org/repo#1", "ip-1", CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── GetActiveIssueIdentifiersAsync ──────────────────────────────────

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_CombinesQueuedAndRunningIssues()
    {
        // Enqueue a job
        _dispatcherService.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "queued-issue",
            IssueProviderId = "ip-q",
            RepoProviderId = "rp-1",
            InitiatedBy = "test",
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        // Active run
        _mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun>
        {
            new()
            {
                RunId = "run-1",
                IssueIdentifier = "running-issue",
                IssueProviderConfigId = "ip-r",
                IssueTitle = "Test",
                RepoProviderConfigId = "rp-1"
            }
        });

        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain(("queued-issue", "ip-q"));
        result.Should().Contain(("running-issue", "ip-r"));
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_WhenEmpty_ReturnsEmptySet()
    {
        _mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun>());

        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_DeduplicatesOverlappingEntries()
    {
        // Same issue in both queue and active runs
        _dispatcherService.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "same-issue",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            InitiatedBy = "test",
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        _mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun>
        {
            new()
            {
                RunId = "run-1",
                IssueIdentifier = "same-issue",
                IssueProviderConfigId = "ip-1",
                IssueTitle = "Test",
                RepoProviderConfigId = "rp-1"
            }
        });

        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result.Should().Contain(("same-issue", "ip-1"));
    }

    // ── Null argument validation ────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_NullRequest_ThrowsArgumentNull()
    {
        var act = () => _sut.DistributeAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateRequest(WorkItemTaskType taskType) => new()
    {
        IssueIdentifier = "org/repo#1",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "loop",
        TaskType = taskType,
        AgentSelector = "dotnet",
        TimeoutSeconds = 3600,
        RunType = PipelineRunType.Implementation,
        IssueDetail = new IssueDetail { Title = "Fix bug", Description = "", Labels = [], Identifier = "org/repo#1" },
        LinkedPullRequest = taskType == WorkItemTaskType.Review
            ? new LinkedPullRequest { BranchName = "feature/pr-1", IsDraft = false, Number = 1, Url = "https://github.com/org/repo/pull/1" }
            : null,
        ReviewPrTargetBranch = taskType == WorkItemTaskType.Review ? "main" : null
    };
}
