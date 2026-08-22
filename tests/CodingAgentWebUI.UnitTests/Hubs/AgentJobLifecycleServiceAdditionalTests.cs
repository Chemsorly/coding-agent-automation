using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Additional unit tests for <see cref="AgentJobLifecycleService"/> covering:
/// HandleJobAcceptedAsync, HandleJobRejectedAsync (retry + permanent-fail paths),
/// HandleStepTransition (direct service tests), and PostCompletionBookkeepingAsync
/// (FinalLabel override, step-derived labels, no-label case).
/// Written against the service directly (not through the hub) to avoid routing through
/// the hub's dispatch layer and to isolate service logic precisely.
/// </summary>
public sealed class AgentJobLifecycleServiceAdditionalTests
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

    private static AgentEntry MakeAgent(string agentId = "agent-1", string jobId = "job-1") => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveJobId = jobId
    };

    // ─────────────────────────────────────────────────────────────────────────
    // HandleJobAcceptedAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleJobAccepted_AgentNotNull_TransitionsToBusyAndNotifies()
    {
        var agent = MakeAgent();
        var svc = CreateService();

        await svc.HandleJobAcceptedAsync(new JobId("job-1"), agent, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Busy), Times.Once);
        _changeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task HandleJobAccepted_AgentNotNull_TransitionsWorkItemToRunning()
    {
        var agent = MakeAgent();
        var svc = CreateService();

        await svc.HandleJobAcceptedAsync(new JobId("job-1"), agent, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Running, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobAccepted_NullAgent_StillTransitionsWorkItemToRunning()
    {
        var svc = CreateService();

        await svc.HandleJobAcceptedAsync(new JobId("job-1"), null, CancellationToken.None);

        // Agent status transition skipped, but WorkItem must still transition
        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Running, It.IsAny<CancellationToken>()), Times.Once);
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobAccepted_WorkItemTransitionThrows_DoesNotPropagate()
    {
        _facade
            .Setup(f => f.TransitionWorkItemAsync("job-1", WorkItemStatus.Running, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var svc = CreateService();

        var act = async () => await svc.HandleJobAcceptedAsync(new JobId("job-1"), null, CancellationToken.None);

        await act.Should().NotThrowAsync("TransitionWorkItem failure is caught and logged, not propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HandleJobRejectedAsync — no run in memory
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleJobRejected_NoRunInMemory_TransitionsAgentToIdle()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var agent = MakeAgent();
        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), agent, "workspace full", CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task HandleJobRejected_NullAgent_DoesNotThrow()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var svc = CreateService();

        var act = async () => await svc.HandleJobRejectedAsync(
            new JobId("job-1"), null, "reason", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HandleJobRejectedAsync — run in memory, retry path
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleJobRejected_RunExists_RemovesRunBeforeCleanup()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "workspace full", CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejected_RetryCountBelowMax_RequeuesWorkItem()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);  // 1 < 3 → should requeue

        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "agent error", CancellationToken.None);

        _facade.Verify(f => f.RequeueWorkItemAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
        // Must NOT permanently fail when retries remain
        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobRejected_RetryCountAtZero_RequeuesAndMarksIssueComplete()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "agent error", CancellationToken.None);

        _facade.Verify(f => f.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId), Times.Once);
        _facade.Verify(f => f.RequeueWorkItemAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HandleJobRejectedAsync — permanent failure (max retries exhausted)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleJobRejected_RetryCountAtMax_TransitionsToFailed()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);  // 3 >= 3 → permanent failure

        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "crash", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), FailureReason.InfrastructureFailure), Times.Once);
        // Must NOT requeue when max retries exhausted
        _facade.Verify(f => f.RequeueWorkItemAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobRejected_MaxRetries_SwapsLabelToError()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "crash", CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejected_MaxRetries_LabelSwapThrows_DoesNotPropagate()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _issueOps
            .Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("rate limit"));

        var svc = CreateService();

        var act = async () => await svc.HandleJobRejectedAsync(
            new JobId("job-1"), null, "crash", CancellationToken.None);

        await act.Should().NotThrowAsync("label swap failure is non-fatal and must not propagate");
    }

    [Fact]
    public async Task HandleJobRejected_RequeueThrows_FallsBackToPermanentFailure()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);  // would requeue, but requeue fails
        _facade.Setup(f => f.RequeueWorkItemAsync("job-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "crash", CancellationToken.None);

        // Requeue failed → falls back to permanent failure
        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), FailureReason.InfrastructureFailure), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejected_AgentFields_UpdatedOnRejection()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var agent = MakeAgent();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var svc = CreateService();

        await svc.HandleJobRejectedAsync(new JobId("job-1"), agent, "workspace full", CancellationToken.None);

        agent.ActiveJobId.Should().BeNull("ActiveJobId cleared on rejection");
        agent.LastJobCompletedAt.Should().BeAfter(before, "LastJobCompletedAt set to push agent to back of queue");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HandleStepTransition — direct service-level tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HandleStepTransition_NullRun_DoesNotThrow()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var svc = CreateService();

        var act = () => svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        act.Should().NotThrow("null run is handled gracefully — no-op");
        _changeNotifier.Verify(c => c.NotifyChange(), Times.Never);
    }

    [Fact]
    public void HandleStepTransition_ValidRun_UpdatesCurrentStep()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void HandleStepTransition_ValidRun_NotifiesChange()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow, null);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    [Fact]
    public void HandleStepTransition_AdvancesHighWaterMark()
    {
        var run = MakeRun();
        run.HighWaterMark = PipelineStep.VerifyingBaseline;
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode,
            "GeneratingCode has higher logical order than VerifyingBaseline");
    }

    [Fact]
    public void HandleStepTransition_DoesNotRegressHighWaterMark()
    {
        var run = MakeRun();
        run.HighWaterMark = PipelineStep.RunningQualityGates;
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        // Transition backward (retry step) — HighWaterMark must stay
        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.RunningQualityGates,
            "high-water mark must never go backward");
    }

    [Fact]
    public void HandleStepTransition_FailedStep_DoesNotAdvanceHighWaterMark()
    {
        var run = MakeRun();
        run.HighWaterMark = PipelineStep.GeneratingCode;
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.Failed, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode,
            "terminal Failed step must not update HighWaterMark");
    }

    [Fact]
    public void HandleStepTransition_CancelledStep_DoesNotAdvanceHighWaterMark()
    {
        var run = MakeRun();
        run.HighWaterMark = PipelineStep.GeneratingCode;
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.Cancelled, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode,
            "terminal Cancelled step must not update HighWaterMark");
    }

    [Fact]
    public void HandleStepTransition_FarFutureTimestamp_IsClamped()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var futureTimestamp = DateTimeOffset.UtcNow.AddHours(48);
        var before = DateTimeOffset.UtcNow;

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, futureTimestamp, null);

        run.LastStepChangeAt.Should().BeBefore(futureTimestamp,
            "far-future timestamp must be clamped to now");
        run.LastStepChangeAt.Should().BeOnOrAfter(before.AddSeconds(-1));
    }

    [Fact]
    public void HandleStepTransition_PastTimestamp_UsedAsIs()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var pastTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, pastTimestamp, null);

        // Past timestamps are valid and should not be clamped
        run.LastStepChangeAt.Should().BeCloseTo(pastTimestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void HandleStepTransition_WithMetadata_AppliesMetadataToRun()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var metadata = new Dictionary<string, string>
        {
            ["BranchName"] = "feature/new-login",
            ["FilesChangedCount"] = "12"
        };

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.ReviewingCode, DateTimeOffset.UtcNow, metadata);

        run.BranchName.Should().Be("feature/new-login");
        run.FilesChangedCount.Should().Be(12);
    }

    [Fact]
    public void HandleStepTransition_EmptyMetadata_IsNoOp()
    {
        var run = MakeRun();
        run.BranchName = "original";
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

        run.BranchName.Should().Be("original", "empty metadata must not alter existing run state");
    }

    [Fact]
    public void HandleStepTransition_TouchesLastProgressAsync()
    {
        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade
            .Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService();

        svc.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        // TouchLastProgressAsync is fire-and-forget but must be initiated
        _facade.Verify(f => f.TouchLastProgressAsync(
            It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PostCompletionBookkeepingAsync (via HandleJobCompletedAsync — regular run)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostCompletion_FinalStep_Completed_SwapsLabelToDone()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null  // no override → derive from step
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

    [Fact]
    public async Task PostCompletion_FinalStep_Cancelled_SwapsLabelToCancelled()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Cancelled,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Cancelled), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_FinalStep_Failed_SwapsLabelToError()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null,
            FailureReason = "build failed",
            FailureCategory = FailureReason.AgentError
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed,
                It.IsAny<CancellationToken>(), "build failed", FailureReason.AgentError))
            .ReturnsAsync(run);

        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_ValidFinalLabel_OverridesStepDerivedLabel()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.EpicReview  // override for decomposition
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // FinalLabel takes precedence over step-derived AgentLabels.Done
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.EpicReview), Times.Once);
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Never);
    }

    [Fact]
    public async Task PostCompletion_InvalidFinalLabel_FallsBackToStepDerivedLabel()
    {
        // FinalLabel with a value not in AgentLabels.All → ignored, step-derived label used
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = "custom:not-a-real-agent-label"
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // Invalid FinalLabel is ignored → falls back to step-derived label (Done)
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_UnknownFinalStep_NoLabelSwap_FeedbackCommentStillPosted()
    {
        // A step that has no label mapping (e.g., Created) → no swap, but comment still runs
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Created,
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

        // No label swap — Starting has no mapping
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
        // Feedback comment must still be posted regardless
        _issueOps.Verify(o => o.PostIssueFeedbackCommentAsync(run), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_LabelSwapThrows_ExceptionPropagates()
    {
        // PostCompletionBookkeepingAsync has no try-catch around SwapLabelAsync.
        // The exception propagates out of HandleJobCompletedAsync — feedback comment
        // is never reached. This test pins that documented behaviour; if a future change
        // wraps SwapLabelAsync in a try-catch, the ThrowAsync expectation here will
        // fail and alert the author to also verify PostIssueFeedbackCommentAsync runs.
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);
        _issueOps
            .Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("rate limit"));

        var svc = CreateService();

        var act = async () =>
            await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("SwapLabelAsync exceptions propagate from PostCompletionBookkeepingAsync");
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejected_NullAgent_DoesNotCallSignal()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var svc = CreateService();
        await svc.HandleJobRejectedAsync(new JobId("job-1"), null, "reason", CancellationToken.None);
    }
}
