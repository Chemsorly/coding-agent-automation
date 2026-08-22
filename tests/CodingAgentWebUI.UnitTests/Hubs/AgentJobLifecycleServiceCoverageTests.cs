using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Additional coverage tests for <see cref="AgentJobLifecycleService"/> targeting
/// previously uncovered branches:
/// - PostCompletionBookkeepingAsync null label (unknown FinalStep, null FinalLabel)
/// - HandleStepTransition with remaining metadata keys not covered by service-level tests
/// - HandleJobCompletedAsync consolidation notification path
/// - HandleJobRejectedAsync TransitionWorkItemAsync throws
/// </summary>
public sealed class AgentJobLifecycleServiceCoverageTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycleManager = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentJobLifecycleService CreateService() => new(
        _facade.Object,
        _lifecycleManager.Object,
        _labelService.Object,
        _issueOps.Object,
        _changeNotifier.Object,
        _logger.Object);

    private static PipelineRun MakeRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    // ─── PostCompletionBookkeepingAsync: null FinalLabel + unknown step ───────

    [Fact]
    public async Task PostCompletion_NullFinalLabel_UnknownStep_NoSwap_FeedbackStillPosted()
    {
        // PipelineStep.Created has no label mapping → label = null → no swap
        // Feedback comment must still be posted
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Created,  // no label mapping
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
        _issueOps.Verify(o => o.PostIssueFeedbackCommentAsync(run), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_NullFinalLabel_AnalyzingCodeStep_NoSwap()
    {
        // PipelineStep.AnalyzingCode has no label mapping
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.AnalyzingCode,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ─── PostCompletionBookkeepingAsync: FinalLabel is null (not in AgentLabels.All) ──

    [Fact]
    public async Task PostCompletion_FinalLabelIsNull_UsesStepDerivedLabel()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null  // explicitly null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
    }

    // ─── HandleJobCompletedAsync: consolidation NotifyChange called ──────────

    [Fact]
    public async Task Consolidation_NotifiesChangeAfterCompletion()
    {
        var run = new PipelineRun
        {
            RunId = "consol-job",
            IssueIdentifier = "consolidation",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1"
        };
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("consol-job")).Returns(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("consol-job"), null, payload, CancellationToken.None);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ─── HandleJobRejectedAsync: TransitionWorkItemAsync throws ─────────────

    [Fact]
    public async Task HandleJobRejected_MaxRetries_TransitionWorkItemThrows_DoesNotPropagate()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3); // max retries

        _facade.Setup(f => f.TransitionWorkItemAsync(
                It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var svc = CreateService();
        var act = async () => await svc.HandleJobRejectedAsync(
            new JobId("job-1"), null, "crash", CancellationToken.None);

        await act.Should().NotThrowAsync("permanent failure transition exception is caught and logged");
    }

    // ─── HandleJobAcceptedAsync: agent is null, NotifyChange not called ──────

    [Fact]
    public async Task HandleJobAccepted_NullAgent_NotifyChangeNotCalled()
    {
        var svc = CreateService();

        await svc.HandleJobAcceptedAsync(new JobId("job-1"), null, CancellationToken.None);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Never,
            "NotifyChange is only called when agent is not null");
    }

    // ─── HandleStepTransition: metadata applied for all remaining keys ───────

    [Theory]
    [InlineData("OpenIssuesDownloaded", 25)]
    [InlineData("DecompositionSubIssuesCreated", 10)]
    [InlineData("DecompositionSubIssuesAttempted", 11)]
    [InlineData("RetryCount", 2)]
    [InlineData("InfrastructureRetryCount", 3)]
    [InlineData("CodeReviewIterationsCompleted", 4)]
    [InlineData("CodeReviewIterationsTotal", 5)]
    [InlineData("CodeReviewIterationInProgress", 1)]
    public void HandleStepTransition_IntMetadataKey_AppliedToRun(string key, int expected)
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { [key] = expected.ToString() });

        var actual = key switch
        {
            "OpenIssuesDownloaded" => run.OpenIssuesDownloaded,
            "DecompositionSubIssuesCreated" => run.DecompositionSubIssuesCreated,
            "DecompositionSubIssuesAttempted" => run.DecompositionSubIssuesAttempted,
            "RetryCount" => run.RetryCount,
            "InfrastructureRetryCount" => run.InfrastructureRetryCount,
            "CodeReviewIterationsCompleted" => run.CodeReviewIterationsCompleted,
            "CodeReviewIterationsTotal" => run.CodeReviewIterationsTotal,
            "CodeReviewIterationInProgress" => run.CodeReviewIterationInProgress,
            _ => throw new ArgumentOutOfRangeException(key)
        };

        actual.Should().Be(expected, $"key '{key}' must set the corresponding property");
    }

    [Fact]
    public void HandleStepTransition_TotalCostMetadata_Applied()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["TotalCost"] = "3.50" });

        run.TotalCost.Should().Be(3.50m);
    }

    [Fact]
    public void HandleStepTransition_TotalTokensMetadata_Applied()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["TotalTokens"] = "1234567890" });

        run.TotalTokens.Should().Be(1234567890L);
    }

    [Fact]
    public void HandleStepTransition_BaselineHealthPassedFalse_Applied()
    {
        var run = MakeRun();
        run.BaselineHealthPassed = true; // start as true
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.VerifyingBaseline, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["BaselineHealthPassed"] = "False" });

        run.BaselineHealthPassed.Should().BeFalse();
    }

    [Fact]
    public void HandleStepTransition_AnalysisSkippedTrue_Applied()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["AnalysisSkipped"] = "True" });

        run.AnalysisSkipped.Should().BeTrue();
    }

    [Fact]
    public void HandleStepTransition_CodeReviewCountsMetadata_Applied()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var unitSep = new string(new[] { (char)31 });
        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.ReviewingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["CodeReviewCriticalCount"] = "5",
                ["CodeReviewWarningCount"] = "10",
                ["CodeReviewSuggestionCount"] = "15",
                ["CodeReviewAgentsRun"] = "agent-a" + unitSep + "agent-b"
            });

        run.CodeReviewCriticalCount.Should().Be(5);
        run.CodeReviewWarningCount.Should().Be(10);
        run.CodeReviewSuggestionCount.Should().Be(15);
        run.CodeReviewAgentsRun.Should().HaveCount(2);
    }

    [Fact]
    public void HandleStepTransition_BranchNameAndFilesChanged_Applied()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["BranchName"] = "feature/my-branch",
                ["FilesChangedCount"] = "7",
                ["LinesAdded"] = "100",
                ["LinesRemoved"] = "20"
            });

        run.BranchName.Should().Be("feature/my-branch");
        run.FilesChangedCount.Should().Be(7);
        run.LinesAdded.Should().Be(100);
        run.LinesRemoved.Should().Be(20);
    }

    // ─── HandleJobCompletedAsync: agent null, no agent state update ──────────

    [Fact]
    public async Task HandleJobCompleted_NullAgent_NoAgentStateChange()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // No TransitionStatus call when agent is null
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }
}
