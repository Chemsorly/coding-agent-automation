using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for AgentJobLifecycleService.
/// Covers: job accepted/rejected/completed lifecycle, step transitions, HighWaterMark,
/// ApplyStepMetadata (internal static), orphaned run handling, and retry exhaustion.
/// </summary>
public sealed class AgentJobLifecycleServiceTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycle = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentJobLifecycleService _sut;

    public AgentJobLifecycleServiceTests()
    {
        _sut = new AgentJobLifecycleService(
            _facade.Object,
            _lifecycle.Object,
            _labelService.Object,
            _issueOps.Object,
            _changeNotifier.Object,
            _logger.Object);
    }

    private static AgentEntry MakeAgent(string agentId = "agent-1") =>
        new()
        {
            AgentId = new AgentId(agentId),
            ConnectionId = $"conn-{agentId}",
            Hostname = "test-host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };

    private static PipelineRun MakeRun(string jobId = "job-1", string issueId = "GH-42") =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = jobId,
            IssueIdentifier = issueId,
            IssueTitle = "Test issue",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

    private static JobCompletionPayload MakePayload(
        PipelineStep step = PipelineStep.Completed,
        string? finalLabel = null,
        FailureReason? failureCategory = null) =>
        new()
        {
            FinalStep = step,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = finalLabel,
            FailureCategory = failureCategory
        };

    // ── HandleJobAcceptedAsync ────────────────────────────────────────────

    [Fact]
    public async Task HandleJobAcceptedAsync_WithAgent_TransitionsToBusy()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Running, It.IsAny<CancellationToken>(),
            null, null)).Returns(Task.CompletedTask);

        await _sut.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Busy), Times.Once);
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task HandleJobAcceptedAsync_WithNullAgent_StillTransitionsWorkItem()
    {
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Running, It.IsAny<CancellationToken>(),
            null, null)).Returns(Task.CompletedTask);

        await _sut.HandleJobAcceptedAsync(jobId, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Running,
            It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task HandleJobAcceptedAsync_WhenTransitionThrows_DoesNotPropagate()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), null, null))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        // Should not throw
        var act = () => _sut.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── HandleJobRejectedAsync ────────────────────────────────────────────

    [Fact]
    public async Task HandleJobRejectedAsync_WhenRunExists_RemovesRun()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _facade.Setup(f => f.RequeueWorkItemAsync(jobId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "timeout", CancellationToken.None);

        _facade.Verify(f => f.RemoveRun(jobId), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenRunExists_TransitionsAgentToIdle()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(MakeRun("job-1"));
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _facade.Setup(f => f.RequeueWorkItemAsync(jobId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Idle), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenNoRunFound_StillTransitionsAgentToIdle()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);

        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Idle), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenMaxRetriesExhausted_PermanentlyFails()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        // RetryCount = 3 (== max) → should NOT requeue, should permanently fail
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error)).Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "crash", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure), Times.Once);
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenRequeueFails_FallsBackToPermanentFail()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _facade.Setup(f => f.RequeueWorkItemAsync(jobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB down"));
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error)).Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure), Times.Once);
    }

    // ── HandleJobCompletedAsync ───────────────────────────────────────────

    [Fact]
    public async Task HandleJobCompletedAsync_WithNonConsolidationRun_TransitionsAgentToIdle()
    {
        var agent = MakeAgent();
        agent.ActiveJobId = "job-1";
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        var payload = MakePayload();

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Idle), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WithFailedStep_SwapsToErrorLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error)).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run)).Returns(Task.CompletedTask);

        var payload = MakePayload(PipelineStep.Failed);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WithCompletedStep_SwapsToDoneLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done)).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run)).Returns(Task.CompletedTask);

        var payload = MakePayload();

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WithCancelledStep_SwapsToCancelledLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Cancelled)).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run)).Returns(Task.CompletedTask);

        var payload = MakePayload(PipelineStep.Cancelled);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Cancelled), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_FinalLabelOverridesTaken_WhenKnownLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error)).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run)).Returns(Task.CompletedTask);

        // FinalLabel = agent:error overrides Completed step
        var payload = MakePayload(finalLabel: AgentLabels.Error);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_UnknownFinalLabel_IgnoredFallsBackToStep()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done)).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run)).Returns(Task.CompletedTask);

        var payload = MakePayload(finalLabel: "custom:unknown");

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenRunNotFound_TriesDbRecovery()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .Returns(Task.CompletedTask);
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string IssueIdentifier, string IssueProviderConfigId)?)null);

        var payload = MakePayload();

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_ConsolidationRun_SkipsBookkeeping()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "job-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId, // consolidation
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        var payload = MakePayload();

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        // Bookkeeping (label swap, feedback comment) should NOT be called for consolidation
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
        _issueOps.Verify(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()), Times.Never);
    }

    // ── HandleStepTransition ──────────────────────────────────────────────

    [Fact]
    public void HandleStepTransition_WhenRunExists_UpdatesCurrentStep()
    {
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.TouchLastProgressAsync(jobId, It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public void HandleStepTransition_WhenNoRun_DoesNothing()
    {
        var jobId = new JobId("no-run");
        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);

        _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        _changeNotifier.Verify(n => n.NotifyChange(), Times.Never);
    }

    [Fact]
    public void HandleStepTransition_ClampsFutureTimestamp()
    {
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var future = DateTimeOffset.UtcNow.AddHours(5);
        _sut.HandleStepTransition(jobId, PipelineStep.AnalyzingCode, future, null);

        run.LastStepChangeAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void HandleStepTransition_AdvancesHighWaterMark()
    {
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        run.HighWaterMark = PipelineStep.Created;
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void HandleStepTransition_DoesNotLowerHighWaterMark()
    {
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        run.HighWaterMark = PipelineStep.GeneratingCode;
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Transition to an earlier step
        _sut.HandleStepTransition(jobId, PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void HandleStepTransition_TerminalStep_DoesNotAdvanceHighWaterMark()
    {
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        run.HighWaterMark = PipelineStep.GeneratingCode;
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _sut.HandleStepTransition(jobId, PipelineStep.Failed, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void HandleStepTransition_WithMetadata_AppliesMetadata()
    {
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var metadata = new Dictionary<string, string>
        {
            ["BranchName"] = "feature/test",
            ["FilesChangedCount"] = "5"
        };

        _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, metadata);

        run.BranchName.Should().Be("feature/test");
        run.FilesChangedCount.Should().Be(5);
    }

    // ── ApplyStepMetadata (internal static) ───────────────────────────────

    [Fact]
    public void ApplyStepMetadata_BranchName_IsApplied()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["BranchName"] = "main" });
        run.BranchName.Should().Be("main");
    }

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_True()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["BaselineHealthPassed"] = "true" });
        run.BaselineHealthPassed.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_False()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["BaselineHealthPassed"] = "false" });
        run.BaselineHealthPassed.Should().BeFalse();
    }

    [Fact]
    public void ApplyStepMetadata_InvalidBoolValue_LeavesNull()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["BaselineHealthPassed"] = "notabool" });
        run.BaselineHealthPassed.Should().BeNull();
    }

    [Fact]
    public void ApplyStepMetadata_AnalysisSkipped_True()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["AnalysisSkipped"] = "true" });
        run.AnalysisSkipped.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepMetadata_FilesChangedCount()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["FilesChangedCount"] = "42" });
        run.FilesChangedCount.Should().Be(42);
    }

    [Fact]
    public void ApplyStepMetadata_InvalidInt_PreservesOriginal()
    {
        var run = MakeRun();
        run.FilesChangedCount = 10;
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["FilesChangedCount"] = "nan" });
        run.FilesChangedCount.Should().Be(10);
    }

    [Fact]
    public void ApplyStepMetadata_LinesAdded_LinesRemoved()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new()
        {
            ["LinesAdded"] = "100",
            ["LinesRemoved"] = "50"
        });
        run.LinesAdded.Should().Be(100);
        run.LinesRemoved.Should().Be(50);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewCounts_SetAtomically()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new()
        {
            ["CodeReviewCriticalCount"] = "3",
            ["CodeReviewWarningCount"] = "7",
            ["CodeReviewSuggestionCount"] = "2"
        });
        run.CodeReviewCriticalCount.Should().Be(3);
        run.CodeReviewWarningCount.Should().Be(7);
        run.CodeReviewSuggestionCount.Should().Be(2);
    }

    [Fact]
    public void ApplyStepMetadata_PartialCodeReviewCounts_PreservesOthers()
    {
        var run = MakeRun();
        run.SetCodeReviewCounts(5, 10, 15);

        // Only override critical
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["CodeReviewCriticalCount"] = "1" });

        run.CodeReviewCriticalCount.Should().Be(1);
        run.CodeReviewWarningCount.Should().Be(10);
        run.CodeReviewSuggestionCount.Should().Be(15);
    }

    [Fact]
    public void ApplyStepMetadata_TotalTokens_Long()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["TotalTokens"] = "999999" });
        run.TotalTokens.Should().Be(999999L);
    }

    [Fact]
    public void ApplyStepMetadata_TotalCost_Decimal()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["TotalCost"] = "1.23" });
        run.TotalCost.Should().Be(1.23m);
    }

    [Fact]
    public void ApplyStepMetadata_TotalCost_InvalidDecimal_PreservesOriginal()
    {
        var run = MakeRun();
        run.TotalCost = 5.0m;
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["TotalCost"] = "notanumber" });
        run.TotalCost.Should().Be(5.0m);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewIterationsCompleted()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new()
        {
            ["CodeReviewIterationsCompleted"] = "2",
            ["CodeReviewIterationsTotal"] = "3",
            ["CodeReviewIterationInProgress"] = "1"
        });
        run.CodeReviewIterationsCompleted.Should().Be(2);
        run.CodeReviewIterationsTotal.Should().Be(3);
        run.CodeReviewIterationInProgress.Should().Be(1);
    }

    [Fact]
    public void ApplyStepMetadata_DecompositionCounts()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new()
        {
            ["DecompositionSubIssuesCreated"] = "10",
            ["DecompositionSubIssuesAttempted"] = "12",
            ["OpenIssuesDownloaded"] = "50"
        });
        run.DecompositionSubIssuesCreated.Should().Be(10);
        run.DecompositionSubIssuesAttempted.Should().Be(12);
        run.OpenIssuesDownloaded.Should().Be(50);
    }

    [Fact]
    public void ApplyStepMetadata_RetryCount_InfrastructureRetryCount()
    {
        var run = MakeRun();
        AgentJobLifecycleService.ApplyStepMetadata(run, new()
        {
            ["RetryCount"] = "2",
            ["InfrastructureRetryCount"] = "1"
        });
        run.RetryCount.Should().Be(2);
        run.InfrastructureRetryCount.Should().Be(1);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewAgentsRun_SplitOnSeparator()
    {
        var run = MakeRun();
        // Use explicit char(31) = U+001F unit separator, same as '\x1F' in production
        var sep = (char)31;
        AgentJobLifecycleService.ApplyStepMetadata(run, new()
        {
            ["CodeReviewAgentsRun"] = $"agent-a{sep}agent-b{sep}agent-c"
        });
        run.CodeReviewAgentsRun.Should().HaveCount(3);
        run.CodeReviewAgentsRun.Should().Contain("agent-a");
        run.CodeReviewAgentsRun.Should().Contain("agent-b");
        run.CodeReviewAgentsRun.Should().Contain("agent-c");
    }

    [Fact]
    public void ApplyStepMetadata_EmptyMetadata_ChangesNothing()
    {
        var run = MakeRun();
        run.BranchName = "original";

        AgentJobLifecycleService.ApplyStepMetadata(run, []);

        run.BranchName.Should().Be("original");
    }

    [Fact]
    public void ApplyStepMetadata_UnknownKey_IsIgnored()
    {
        var run = MakeRun();
        // Should not throw
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["UnknownKey"] = "whatever" });
    }
}
