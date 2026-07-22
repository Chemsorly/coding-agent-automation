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
/// Verifies facade delegation to underlying services with strict argument matching.
/// </summary>
public class LegacyWorkDistributorTests
{
    private readonly Mock<IJobDispatcher> _mockJobDispatcher = new();
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();
    private readonly JobDeduplicationGuardService _dispatcherService;
    private readonly LegacyWorkDistributor _sut;

    public LegacyWorkDistributorTests()
    {
        var logger = Mock.Of<ILogger>();
        var registry = new CodingAgentWebUI.Orchestration.Registry.AgentRegistryService(logger);
        _dispatcherService = new JobDeduplicationGuardService(registry, logger);

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
                "org/repo#1", "ip-1", "rp-1", (string?)null, (string?)null, "loop",
                It.IsAny<CancellationToken>(), "Fix bug", (PipelineProject?)null))
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
                It.Is<ReviewDispatchRequest>(r =>
                    r.PrIdentifier == "org/repo#1" &&
                    r.PrBranchName == "feature/pr-1" &&
                    r.PrTitle == "Fix bug" &&
                    r.PrDescription == "PR body text" &&
                    r.PrAuthor == "author-user" &&
                    r.PrUrl == "https://github.com/org/repo/pull/1" &&
                    r.PrTargetBranch == "main" &&
                    r.IssueProviderId == "ip-1" &&
                    r.RepoProviderId == "rp-1" &&
                    r.BrainProviderId == null &&
                    r.InitiatedBy == "loop"),
                It.IsAny<CancellationToken>(),
                (PipelineProject?)null))
            .ReturnsAsync(true);

        var request = CreateRequest(WorkItemTaskType.Review);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockJobDispatcher.Verify(d => d.TryDispatchReviewAsync(
            It.Is<ReviewDispatchRequest>(r =>
                r.PrIdentifier == "org/repo#1" &&
                r.PrBranchName == "feature/pr-1" &&
                r.PrTitle == "Fix bug" &&
                r.PrDescription == "PR body text" &&
                r.PrAuthor == "author-user" &&
                r.PrUrl == "https://github.com/org/repo/pull/1" &&
                r.PrTargetBranch == "main" &&
                r.IssueProviderId == "ip-1" &&
                r.RepoProviderId == "rp-1" &&
                r.BrainProviderId == null &&
                r.InitiatedBy == "loop"),
            It.IsAny<CancellationToken>(),
            (PipelineProject?)null), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_Decomposition_DelegatesToTryDispatchDecompositionAsync()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchDecompositionAsync(
                "org/repo#1", "Fix bug", PipelineRunType.Implementation,
                "ip-1", "rp-1", (string?)null, "loop",
                It.IsAny<CancellationToken>(),
                (string?)null, (PipelineProject?)null))
            .ReturnsAsync(true);

        var request = CreateRequest(WorkItemTaskType.Decomposition);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockJobDispatcher.Verify(d => d.TryDispatchDecompositionAsync(
            "org/repo#1", "Fix bug", PipelineRunType.Implementation,
            "ip-1", "rp-1", (string?)null, "loop",
            It.IsAny<CancellationToken>(),
            (string?)null, (PipelineProject?)null), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_UnknownTaskType_RoutesToDefaultImplementationPath()
    {
        _mockJobDispatcher.Setup(d => d.TryDispatchAsync(
                "org/repo#1", "ip-1", "rp-1", (string?)null, (string?)null, "loop",
                It.IsAny<CancellationToken>(), "Fix bug", (PipelineProject?)null))
            .ReturnsAsync(true);

        var request = CreateRequest((WorkItemTaskType)99);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        _mockJobDispatcher.Verify(d => d.TryDispatchAsync(
            "org/repo#1", "ip-1", "rp-1", (string?)null, (string?)null, "loop",
            It.IsAny<CancellationToken>(), "Fix bug", (PipelineProject?)null), Times.Once);
        _mockJobDispatcher.Verify(d => d.TryDispatchReviewAsync(
            It.IsAny<ReviewDispatchRequest>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<PipelineProject?>()), Times.Never);
        _mockJobDispatcher.Verify(d => d.TryDispatchDecompositionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<PipelineProject?>()), Times.Never);
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

    // ── Consolidation ─────────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_Consolidation_EnqueuesIntoJobDeduplicationGuardService()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "crun-123",
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "dotnet,kiro",
            TimeoutSeconds = 0,
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationTemplateId = "template-1",
            ConsolidationWorkspacePath = "/tmp/ws"
        };

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeTrue();
        result.ErrorMessage.Should().Be("Queued — no idle agent");

        // Verify enqueued PendingJob field values
        var queuedJobs = _dispatcherService.GetQueuedJobs();
        queuedJobs.Should().HaveCount(1);
        var job = queuedJobs[0];
        job.IssueIdentifier.Should().Be("crun-123");
        job.IssueProviderId.Should().Be("consolidation");
        job.RepoProviderId.Should().Be("");
        job.InitiatedBy.Should().Be("consolidation");
        job.RequiredLabels.Should().BeEquivalentTo(new[] { "dotnet", "kiro" });
        job.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        job.ConsolidationRunType.Should().Be(ConsolidationRunType.BrainConsolidation);
        job.ConsolidationTemplateId.Should().Be("template-1");
        job.ConsolidationWorkspacePath.Should().Be("/tmp/ws");
        job.AutoDispatch.Should().BeFalse();
    }

    [Fact]
    public async Task DistributeAsync_Consolidation_DedupRejectsDuplicate()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "crun-dedup",
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "",
            TimeoutSeconds = 0,
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationWorkspacePath = "/tmp/ws"
        };

        var result1 = await _sut.DistributeAsync(request, CancellationToken.None);
        var result2 = await _sut.DistributeAsync(request, CancellationToken.None);

        result1.Success.Should().BeTrue();
        result2.Success.Should().BeFalse("duplicate should be rejected by dedup");
    }

    [Fact]
    public async Task DistributeAsync_Consolidation_SetsRequiredLabelsFromAgentSelector()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "crun-labels",
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "dotnet, kiro",
            TimeoutSeconds = 0,
            ConsolidationRunType = ConsolidationRunType.HarnessSuggestions,
            ConsolidationWorkspacePath = "/tmp/ws"
        };

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        // Verify the job was enqueued with correctly parsed labels
        var queuedJobs = _dispatcherService.GetQueuedJobs();
        queuedJobs.Should().HaveCount(1);
        queuedJobs[0].RequiredLabels.Should().BeEquivalentTo(new[] { "dotnet", "kiro" });
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
        ReviewPrTargetBranch = taskType == WorkItemTaskType.Review ? "main" : null,
        ReviewPrDescription = taskType == WorkItemTaskType.Review ? "PR body text" : null,
        ReviewPrAuthor = taskType == WorkItemTaskType.Review ? "author-user" : null
    };
}
