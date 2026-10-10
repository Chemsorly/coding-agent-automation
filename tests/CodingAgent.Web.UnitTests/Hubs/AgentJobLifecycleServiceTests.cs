using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Unit tests for AgentJobLifecycleService.
/// Covers: job accepted/rejected/completed lifecycle, step transitions, HighWaterMark,
/// ApplyStepMetadata (internal static), orphaned run handling, and retry exhaustion.
/// </summary>
[Collection("Metrics")]
public sealed class AgentJobLifecycleServiceTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycle = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IHostApplicationLifetime> _appLifetime = new();
    private readonly Mock<IFeedbackCommentOutbox> _outbox = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentJobLifecycleService _sut;

    public AgentJobLifecycleServiceTests()
    {
        // ApplicationStopping is used by PostCompletionBookkeepingAsync to link cancellation tokens.
        // Provide a non-cancellable token so bookkeeping is not aborted in tests.
        _appLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        _sut = new AgentJobLifecycleService(
            new AgentJobLifecycleServiceDependencies(
                _facade.Object,
                _lifecycle.Object,
                _labelService.Object,
                _issueOps.Object,
                _changeNotifier.Object,
                _appLifetime.Object,
                _outbox.Object,
                _logger.Object));
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

    /// <summary>A run built directly, without an agent assigned.</summary>
    private static PipelineRun MakeUnassignedRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    /// <summary>A busy agent that holds <paramref name="jobId"/>.</summary>
    private static AgentEntry MakeBusyAgent(string agentId = "agent-1", string jobId = "job-1") => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveJobId = jobId
    };

    // ── HandleJobAcceptedAsync ────────────────────────────────────────────

    [Fact]
    public async Task HandleJobAcceptedAsync_WithAgent_TransitionsToBusy()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Running, It.IsAny<CancellationToken>(),
            null, null)).ReturnsAsync(true);

        await _sut.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Busy), Times.Once);
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task HandleJobAcceptedAsync_WithNullAgent_StillTransitionsWorkItem()
    {
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Running, It.IsAny<CancellationToken>(),
            null, null)).ReturnsAsync(true);

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

    [Fact]
    public async Task HandleJobAcceptedAsync_WhenTransitionThrows_AgentRemainsInPriorStatus_NotifyChangeNotCalled()
    {
        // Arrange: TransitionWorkItemAsync throws (DB failure)
        // TODO: This test covers only the exception path. The false-return path is covered by
        // HandleJobAcceptedAsync_WhenTransitionReturnsFalse_AgentRemainsInPriorStatus_NotifyChangeNotCalled.
        // Both paths must remain in sync if the guard logic is ever refactored.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), null, null))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        // Act
        await _sut.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);

        // Assert: agent is NOT marked Busy and the UI is NOT notified
        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Busy), Times.Never,
            "Agent must NOT be marked Busy when the WorkItem DB transition fails");
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Never,
            "NotifyChange must NOT be called when the WorkItem DB transition fails");
    }

    [Fact]
    public async Task HandleJobAcceptedAsync_WhenTransitionReturnsFalse_AgentRemainsInPriorStatus_NotifyChangeNotCalled()
    {
        // Arrange: TransitionWorkItemAsync returns false — transition was rejected (e.g. WorkItem
        // already in a terminal state). This is the "silent failure" path distinct from exceptions.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(false);

        // Act
        await _sut.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);

        // Assert: agent is NOT marked Busy and the UI is NOT notified
        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Busy), Times.Never,
            "Agent must NOT be marked Busy when the WorkItem DB transition is rejected");
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Never,
            "NotifyChange must NOT be called when the WorkItem DB transition is rejected");
    }

    [Fact]
    public async Task HandleJobAccepted_NullAgent_NotifyChangeNotCalled()
    {
        _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);

        await _sut.HandleJobAcceptedAsync(new JobId("job-1"), null, CancellationToken.None);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Never,
            "NotifyChange is only called when agent is not null");
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
            .ReturnsAsync(true);
        _labelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "crash", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure), Times.Once);
        _labelService.Verify(l => l.SwapLabelAsync(
            It.Is<ProviderConfigId>(p => p.Value == run.IssueProviderConfigId),
            It.Is<IssueIdentifier>(i => i.Value == run.IssueIdentifier.Value),
            AgentLabels.Error,
            LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_ReviewRetriesExhausted_MarksThePullRequestNotAnIssue()
    {
        // A review's labels live on its pull request in the repository. Marking issue #5 in the tracker
        // would mark an unrelated issue wherever pull requests are numbered separately, as in GitLab.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunId = "job-1",
            IssueIdentifier = "5",
            IssueTitle = "Review !5",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1",
            RunType = PipelineRunType.Review
        });
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure))
            .ReturnsAsync(true);
        _labelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "crash", CancellationToken.None);

        _labelService.Verify(l => l.SwapLabelAsync(
            It.Is<ProviderConfigId>(p => p.Value == "repo-provider-1"),
            It.Is<IssueIdentifier>(i => i.Value == "5"),
            AgentLabels.Error,
            LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Never);
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
            .ReturnsAsync(true);
        // TODO: [WARNING] This uses the 2-arg SwapLabelAsync overload (no CancellationToken). The
        // production call site is 3-arg: SwapLabelAsync(run, AgentLabels.Error, ct). Moq matches
        // overloads by parameter count, so this setup does NOT match the actual call. Change to:
        //   _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()))
        //       .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error)).Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure), Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenCleanupThrows_AgentIsResetAndExceptionPropagates()
    {
        // Arrange: GetWorkItemRetryCountAsync throws — simulates a DB failure inside
        // HandleRejectedRunCleanupAsync. The finally block must reset the agent AND the
        // exception must propagate out of HandleJobRejectedAsync (not be swallowed).
        var agent = MakeAgent();
        agent.ActiveJobId = "job-1"; // non-null so the ActiveJobId null assertion is non-trivial
        agent.Status = AgentStatus.Busy;
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(MakeRun("job-1"));
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        // Act + Assert: exception propagates
        var act = () => _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("the exception from cleanup must not be swallowed");

        // Assert: agent state was reset despite the exception
        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Idle), Times.Once,
            "TransitionStatus(Idle) must be called even when HandleRejectedRunCleanupAsync throws");
        agent.ActiveJobId.Should().BeNull(
            "ActiveJobId must be cleared even when HandleRejectedRunCleanupAsync throws");
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenCleanupThrows_AndAgentIsNull_ExceptionStillPropagates()
    {
        // Arrange: agent is null and cleanup throws — the finally block guard (agent is not null)
        // must not shadow the original exception with a NullReferenceException.
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(MakeRun("job-1"));
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        // Act + Assert: original exception propagates cleanly (no NullReferenceException from finally)
        var act = () => _sut.HandleJobRejectedAsync(jobId, null, "reason", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "the original exception must propagate even when agent is null");
    }

    [Fact]
    public async Task HandleJobRejected_NoRunInMemory_TransitionsAgentToIdle()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var agent = MakeBusyAgent();

        await _sut.HandleJobRejectedAsync(new JobId("job-1"), agent, "workspace full", CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task HandleJobRejected_NullAgent_DoesNotThrow()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var act = async () => await _sut.HandleJobRejectedAsync(
            new JobId("job-1"), null, "reason", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
    [Fact]
    public async Task HandleJobRejected_RetryCountBelowMax_RequeuesWorkItem()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);  // 1 < 3 → should requeue

        await _sut.HandleJobRejectedAsync(new JobId("job-1"), null, "agent error", CancellationToken.None);

        _facade.Verify(f => f.RequeueWorkItemAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
        // Must NOT permanently fail when retries remain
        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Never);
    }
    [Fact]
    public async Task HandleJobRejected_RetryCountAtMax_TransitionsToFailed()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);  // 3 >= 3 → permanent failure

        await _sut.HandleJobRejectedAsync(new JobId("job-1"), null, "crash", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), FailureReason.InfrastructureFailure), Times.Once);
        // Must NOT requeue when max retries exhausted
        _facade.Verify(f => f.RequeueWorkItemAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobRejected_AgentFields_UpdatedOnRejection()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var agent = MakeBusyAgent();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await _sut.HandleJobRejectedAsync(new JobId("job-1"), agent, "workspace full", CancellationToken.None);

        agent.ActiveJobId.Should().BeNull("ActiveJobId cleared on rejection");
        agent.LastJobCompletedAt.Should().BeAfter(before, "LastJobCompletedAt set to push agent to back of queue");
    }

    [Fact]
    public async Task HandleJobRejected_MaxRetries_TransitionWorkItemThrows_DoesNotPropagate()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3); // max retries

        _facade.Setup(f => f.TransitionWorkItemAsync(
                It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var act = async () => await _sut.HandleJobRejectedAsync(
            new JobId("job-1"), null, "crash", CancellationToken.None);

        await act.Should().NotThrowAsync("permanent failure transition exception is caught and logged");
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
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        // CompleteRunAsync must return the run (non-null) so runWasAlive=true → label swap fires
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var payload = MakePayload(PipelineStep.Failed);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WithCompletedStep_SwapsToDoneLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        // CompleteRunAsync must return the run (non-null) so runWasAlive=true → label swap fires
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var payload = MakePayload();

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WithCancelledStep_SwapsToCancelledLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        // CompleteRunAsync must return the run (non-null) so runWasAlive=true → label swap fires
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Cancelled, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var payload = MakePayload(PipelineStep.Cancelled);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_UnknownFinalLabel_IgnoredFallsBackToStep()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        // CompleteRunAsync must return the run (non-null) so runWasAlive=true → label swap fires
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var payload = MakePayload(finalLabel: "custom:unknown");

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_Failed_WithValidFinalLabel_HonorsFinalLabel()
    {
        // Characterization test (issue #3261): FinalLabel override must work for the Failed step,
        // not only for Completed. Before extraction this was the inline two-variable pattern;
        // after extraction it is handled by CompletionOutcomeResolver.ResolveAgentLabel.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // FinalLabel = agent:needs-refinement overrides the Failed-step default (agent:error)
        var payload = MakePayload(PipelineStep.Failed, finalLabel: AgentLabels.NeedsRefinement);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        // TODO: [WARNING] These assertions are only meaningful when CompleteRunAsync returns a non-null run
        // (runWasAlive=true, skipLabelSwap=false). If CompleteRunAsync were to return null, SwapLabelAsync
        // would be silently skipped and the Times.Once assertion would never fire, making this test vacuously
        // pass. Consider adding: _lifecycle.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed,
        // It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once)
        // to catch a null-return regression. See review findings (TestQualityReviewer).
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_Cancelled_WithValidFinalLabel_HonorsFinalLabel()
    {
        // Characterization test (issue #3261): FinalLabel override must work for the Cancelled step.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // FinalLabel = agent:needs-refinement overrides the Cancelled-step default (agent:cancelled)
        var payload = MakePayload(PipelineStep.Cancelled, finalLabel: AgentLabels.NeedsRefinement);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        // TODO: [WARNING] These assertions are only meaningful when CompleteRunAsync returns a non-null run
        // (runWasAlive=true, skipLabelSwap=false). If CompleteRunAsync were to return null, SwapLabelAsync
        // would be silently skipped and the Times.Once assertion would never fire, making this test vacuously
        // pass. Consider adding: _lifecycle.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Cancelled,
        // It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once)
        // to catch a null-return regression. See review findings (TestQualityReviewer).
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Cancelled, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_Failed_WithUnknownFinalLabel_FallsBackToAgentError()
    {
        // Characterization test (issue #3261): an unknown FinalLabel must not be used —
        // the outcome-based fallback (agent:error for Failed) applies.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        _lifecycle.Setup(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>())).ReturnsAsync(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var payload = MakePayload(PipelineStep.Failed, finalLabel: "not-an-agent-label");

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        // TODO: [WARNING] These assertions are only meaningful when CompleteRunAsync returns a non-null run
        // (runWasAlive=true, skipLabelSwap=false). If CompleteRunAsync were to return null, SwapLabelAsync
        // would be silently skipped and the Times.Once assertion would never fire, making this test vacuously
        // pass. Consider adding: _lifecycle.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed,
        // It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once)
        // to catch a null-return regression. See review findings (TestQualityReviewer).
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(o => o.SwapLabelAsync(run, "not-an-agent-label" as string, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenRunNotFound_TriesDbRecovery()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

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
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _issueOps.Verify(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostCompletion_FinalStep_Failed_SwapsLabelToError()
    {
        var run = MakeUnassignedRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null,
            FailureReason = "build failed",
            FailureCategory = FailureReason.AgentError
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycle
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed,
                It.IsAny<CancellationToken>(), "build failed", FailureReason.AgentError))
            .ReturnsAsync(run);

        await _sut.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_ValidFinalLabel_OverridesStepDerivedLabel()
    {
        var run = MakeUnassignedRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.EpicReview  // override for decomposition
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycle
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        await _sut.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // FinalLabel takes precedence over step-derived AgentLabels.Done
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.EpicReview, It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostCompletion_UnknownFinalStep_NoLabelSwap_FeedbackCommentStillPosted()
    {
        // A step that has no label mapping (e.g., Created) → no swap, but comment still runs
        var run = MakeUnassignedRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Created,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycle
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        await _sut.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // No label swap — Starting has no mapping
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Feedback comment must still be posted regardless
        _issueOps.Verify(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostCompletion_LabelSwapThrows_ExceptionPropagates()
    {
        // PostCompletionBookkeepingAsync has no try-catch around SwapLabelAsync.
        // The exception propagates out of HandleJobCompletedAsync — feedback comment
        // is never reached. This test pins that documented behaviour; if a future change
        // wraps SwapLabelAsync in a try-catch, the ThrowAsync expectation here will
        // fail and alert the author to also verify PostIssueFeedbackCommentAsync runs.
        var run = MakeUnassignedRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycle
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);
        _issueOps
            .Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limit"));

        var act = async () =>
            await _sut.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("SwapLabelAsync exceptions propagate from PostCompletionBookkeepingAsync");
        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

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

        await _sut.HandleJobCompletedAsync(new JobId("consol-job"), null, payload, CancellationToken.None);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ── HandleJobCompletedAsync — agent null fallback ─────────────────────

    [Fact]
    public async Task HandleJobCompletedAsync_AgentIsNull_AndRunHasAgentId_TransitionsAgentToIdle()
    {
        // Arrange: agent lookup returned null (connection dropped / hash expired),
        // but the run knows which agent was assigned.
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1"); // AgentId = "agent-1" from MakeRun

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = MakePayload();

        // Act: agent is null — simulates registry lookup race
        await _sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

        // Assert: fallback path clears activeJobId and transitions to Idle using run's AgentId
        var expectedAgentId = new AgentId("agent-1");
        _facade.Verify(f => f.TransitionStatus(expectedAgentId, AgentStatus.Idle), Times.Once);
        _facade.Verify(f => f.UpdateAgentFieldAsync(expectedAgentId, "activeJobId", null), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_AgentIsNull_AndRunHasAgentId_LogsWarning()
    {
        // Arrange
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1"); // AgentId = "agent-1"

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = MakePayload();

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

        // Assert: a Warning is logged with the job ID and agent ID.
        // The log call is: _logger.Warning("{template}", jobId.Value /*string*/, run.AgentId /*string*/)
        // Compiler selects Warning<T0, T1>(string, T0, T1) — use typed matchers per brain entry dotnet.md#moq-serilog.
        _logger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("agent lookup returned null") && s.Contains("{JobId}") && s.Contains("{AgentId}")),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_AgentIsNull_AndRunAgentIdIsNull_DoesNotCallTransitionStatus()
    {
        // Arrange: run exists but was never assigned to an agent (AgentId = null)
        var jobId = new JobId("job-1");
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "job-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            // AgentId intentionally omitted → PipelineRun.AgentId = null
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = MakePayload();

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

        // Assert: no fallback fires — TransitionStatus must never be called
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_AgentIsNull_AndRunIsNull_DoesNotCallTransitionStatus()
    {
        // Arrange: orphaned run path — run was already cleaned up (RevertFailedDistributionAsync)
        var jobId = new JobId("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, It.IsAny<WorkItemStatus>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var payload = MakePayload();

        // Act: agent is null, run is null — no fallback is possible
        await _sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

        // Assert: TransitionStatus must never be called
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
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

    /// <summary>
    /// Issue #3109: housekeeping skips the PR branches of active runs by reading
    /// WorkItems.BranchName, which was written only at completion. A step transition that
    /// carries the branch must store it, also on a replica that holds no in-memory run.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandleStepTransition_WithBranchMetadata_RecordsBranchName(bool runInMemory)
    {
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(runInMemory ? MakeRun("job-1") : null);

        _sut.HandleStepTransition(jobId, PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["BranchName"] = "feature/auto-3109-keyboard-75e8ebe9" });

        _facade.Verify(f => f.RecordBranchNameAsync(
            jobId, "feature/auto-3109-keyboard-75e8ebe9", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HandleStepTransition_WithoutBranchMetadata_DoesNotRecordBranchName()
    {
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(MakeRun("job-1"));

        _sut.HandleStepTransition(jobId, PipelineStep.CreatingBranch, DateTimeOffset.UtcNow, null);

        _facade.Verify(f => f.RecordBranchNameAsync(
            It.IsAny<JobId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public void HandleStepTransition_CancelledStep_DoesNotAdvanceHighWaterMark()
    {
        var run = MakeUnassignedRun();
        run.HighWaterMark = PipelineStep.GeneratingCode;
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.Cancelled, DateTimeOffset.UtcNow, null);

        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode,
            "terminal Cancelled step must not update HighWaterMark");
    }

    [Fact]
    public void HandleStepTransition_PastTimestamp_UsedAsIs()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var pastTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, pastTimestamp, null);

        // Past timestamps are valid and should not be clamped
        run.LastStepChangeAt.Should().BeCloseTo(pastTimestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void HandleStepTransition_EmptyMetadata_IsNoOp()
    {
        var run = MakeUnassignedRun();
        run.BranchName = "original";
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

        run.BranchName.Should().Be("original", "empty metadata must not alter existing run state");
    }

    [Fact]
    public void HandleStepTransition_TouchesLastProgressAsync()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade
            .Setup(f => f.TouchLastProgressAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        // TouchLastProgressAsync is fire-and-forget but must be initiated
        _facade.Verify(f => f.TouchLastProgressAsync(
            It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

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
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
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
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["TotalCost"] = "3.50" });

        run.TotalCost.Should().Be(3.50m);
    }

    [Fact]
    public void HandleStepTransition_TotalTokensMetadata_Applied()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["TotalTokens"] = "1234567890" });

        run.TotalTokens.Should().Be(1234567890L);
    }

    [Fact]
    public void HandleStepTransition_BaselineHealthPassedFalse_Applied()
    {
        var run = MakeUnassignedRun();
        run.BaselineHealthPassed = true; // start as true
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.VerifyingBaseline, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["BaselineHealthPassed"] = "False" });

        run.BaselineHealthPassed.Should().BeFalse();
    }

    [Fact]
    public void HandleStepTransition_AnalysisSkippedTrue_Applied()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
            new JobId("job-1"), PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["AnalysisSkipped"] = "True" });

        run.AnalysisSkipped.Should().BeTrue();
    }

    [Fact]
    public void HandleStepTransition_CodeReviewCountsMetadata_Applied()
    {
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var unitSep = new string(new[] { (char)31 });
        _sut.HandleStepTransition(
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
        var run = MakeUnassignedRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        _sut.HandleStepTransition(
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

    // ── ApplyStepMetadata (internal static) ───────────────────────────────

    [Fact]
    public void ApplyStepMetadata_BranchName_IsApplied()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["BranchName"] = "main" });
        run.BranchName.Should().Be("main");
    }

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_True()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["BaselineHealthPassed"] = "true" });
        run.BaselineHealthPassed.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_False()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["BaselineHealthPassed"] = "false" });
        run.BaselineHealthPassed.Should().BeFalse();
    }

    [Fact]
    public void ApplyStepMetadata_InvalidBoolValue_LeavesNull()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["BaselineHealthPassed"] = "notabool" });
        run.BaselineHealthPassed.Should().BeNull();
    }

    [Fact]
    public void ApplyStepMetadata_AnalysisSkipped_True()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["AnalysisSkipped"] = "true" });
        run.AnalysisSkipped.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepMetadata_FilesChangedCount()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["FilesChangedCount"] = "42" });
        run.FilesChangedCount.Should().Be(42);
    }

    [Fact]
    public void ApplyStepMetadata_InvalidInt_PreservesOriginal()
    {
        var run = MakeRun();
        run.FilesChangedCount = 10;
        StepMetadataApplier.Apply(run, new() { ["FilesChangedCount"] = "nan" });
        run.FilesChangedCount.Should().Be(10);
    }

    [Fact]
    public void ApplyStepMetadata_LinesAdded_LinesRemoved()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new()
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
        StepMetadataApplier.Apply(run, new()
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
        StepMetadataApplier.Apply(run, new() { ["CodeReviewCriticalCount"] = "1" });

        run.CodeReviewCriticalCount.Should().Be(1);
        run.CodeReviewWarningCount.Should().Be(10);
        run.CodeReviewSuggestionCount.Should().Be(15);
    }

    [Fact]
    public void ApplyStepMetadata_TotalTokens_Long()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["TotalTokens"] = "999999" });
        run.TotalTokens.Should().Be(999999L);
    }

    [Fact]
    public void ApplyStepMetadata_TotalCost_Decimal()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new() { ["TotalCost"] = "1.23" });
        run.TotalCost.Should().Be(1.23m);
    }

    [Fact]
    public void ApplyStepMetadata_TotalCost_InvalidDecimal_PreservesOriginal()
    {
        var run = MakeRun();
        run.TotalCost = 5.0m;
        StepMetadataApplier.Apply(run, new() { ["TotalCost"] = "notanumber" });
        run.TotalCost.Should().Be(5.0m);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewIterationsCompleted()
    {
        var run = MakeRun();
        StepMetadataApplier.Apply(run, new()
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
        StepMetadataApplier.Apply(run, new()
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
        StepMetadataApplier.Apply(run, new()
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
        StepMetadataApplier.Apply(run, new()
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

        StepMetadataApplier.Apply(run, []);

        run.BranchName.Should().Be("original");
    }

    [Fact]
    public void ApplyStepMetadata_UnknownKey_IsIgnored()
    {
        var run = MakeRun();
        var branchBefore = run.BranchName;
        StepMetadataApplier.Apply(run, new() { ["UnknownKey"] = "whatever" });
        // State must be unchanged — unknown keys are silently ignored
        run.BranchName.Should().Be(branchBefore);
        run.RetryCount.Should().Be(0);
    }

    // ── Fire-and-forget UpdateAgentFieldAsync fault logging ───────────────
    // TODO: [WARNING] Two tests were removed from this class during the fire-and-forget logging
    // change and have no surviving equivalent anywhere in the test suite:
    //   - HandleJobCompletedAsync_WhenCancelled_PostCompletionBookkeepingDoesNotThrow: verified that
    //     OperationCanceledException thrown inside PostCompletionBookkeepingAsync is caught and does
    //     not propagate to the hub caller. This cancellation-swallowing path is now untested.
    //   - HandleJobCompletedAsync_WhenSwapLabelThrowsInvalidOperation_StillPropagates: verified that
    //     non-cancellation exceptions from SwapLabelAsync continue to propagate (sentinel boundary).
    //     AgentJobLifecycleServiceAdditionalTests.PostCompletion_LabelSwapThrows_ExceptionPropagates
    //     covers the propagation case, but the OperationCanceledException swallowing case is absent.
    // Re-add a test for the cancellation-swallowing behaviour in PostCompletionBookkeepingAsync.

    [Fact]
    public async Task HandleJobRejectedAsync_WhenUpdateAgentFieldFaults_LogsWarning()
    {
        // Arrange: the activeJobId Redis write fails.
        // TCS is signalled by the Moq Callback when the matching Warning fires on the thread pool,
        // so the test thread yields instead of spinning — no SpinWait, no CPU pressure, no torn reads.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        // Wire the Callback before the act so the TCS is signalled as soon as the continuation fires.
        // Serilog's Warning<T0,T1,T2>(Exception?, string, T0, T1, T2) overload is matched by concrete types:
        // T0 = string (callerContext), T1 = AgentId, T2 = string (field name). It.IsAny<object>() would NOT match here.
        // T2 is pinned to "activeJobId" so the TCS only fires for the correct block —
        // both blocks in ResetAgentToIdle share the same callerContext ("ResetAgentToIdle").
        // TODO [WARNING]: Serilog's Warning<T0,T1,T2> generic overload must be resolved exactly by Moq.
        // If the mock resolves the non-generic Warning(Exception?, string, params object[]) overload instead,
        // the Setup Callback will never fire and the test will time out after 30 s rather than fail fast.
        // If unexpected 30 s hangs appear on CI, verify that Moq is resolving Warning<string,AgentId,string>
        // specifically (not the params-array overload). Check mock library version for generic overload resolution.
        _logger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("ResetAgentToIdle")),
                It.IsAny<AgentId>(),
                It.Is<string>(f => f == "activeJobId")))
            .Callback(() => warningFired.TrySetResult(true));

        // Act
        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);

        // Await the TCS with a 30 s safety net. Task.FromException produces an already-faulted task,
        // so the ContinueWith callback is queued to the thread pool immediately. The test thread
        // blocks here without spinning, giving the thread pool uncontested time to drain the callback.
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: a Warning is logged with the exception, callerContext, AgentId, and field name.
        // T2 matcher pins to "activeJobId" to confirm the correct fault path fired, not just any
        // matching template (both ContinueWith blocks in ResetAgentToIdle share the same callerContext).
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("ResetAgentToIdle")),  // T0 = string (callerContext)
                It.IsAny<AgentId>(),                                      // T1 = AgentId
                It.Is<string>(f => f == "activeJobId")),                  // T2 = string (field name) — pin to faulted field
            Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenLastJobCompletedAtUpdateFaults_LogsWarning()
    {
        // Arrange: the lastJobCompletedAt Redis write fails.
        // activeJobId is explicitly configured to succeed so only one Warning fires.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null))
            .Returns(Task.CompletedTask);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        _logger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("ResetAgentToIdle")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))   // TODO [WARNING]: T2 (field) should be pinned to "lastJobCompletedAt" (mirroring the first
                                       // test which pins to "activeJobId"). Both calls in ResetAgentToIdle share the same
                                       // callerContext, so a broad It.IsAny<string>() could match the activeJobId warning if that
                                       // fault also fires, causing Times.Once to fail spuriously. Pin to "lastJobCompletedAt".
            .Callback(() => warningFired.TrySetResult(true));

        // Act
        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: a Warning is logged for the lastJobCompletedAt fault path.
        // Times.Once is correct here: TCS WaitAsync does not return until the Callback has fired,
        // so exactly one Warning has been recorded before Verify runs. AtLeastOnce was a defensive
        // choice under the old SpinWait approach where the count was uncertain; it is no longer needed.
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("ResetAgentToIdle")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),   // TODO [WARNING]: pin to "lastJobCompletedAt" here as well (see Setup comment above).
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenActiveJobIdUpdateFaults_LogsWarning()
    {
        // Arrange: the activeJobId Redis write fails in HandleJobCompletedAsync (agent path).
        // orphanRestoredAt and lastJobCompletedAt are not configured to fault, so only one Warning fires.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        _logger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: a Warning is logged for the activeJobId fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenOrphanRestoredAtUpdateFaults_LogsWarning()
    {
        // Arrange: the orphanRestoredAt Redis write fails in HandleJobCompletedAsync (agent path).
        // activeJobId and lastJobCompletedAt are not configured to fault.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        _logger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: a Warning is logged for the orphanRestoredAt fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenLastJobCompletedAtUpdateFaults_LogsWarning()
    {
        // Arrange: the lastJobCompletedAt Redis write fails in HandleJobCompletedAsync (agent path).
        // activeJobId and orphanRestoredAt are not configured to fault.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        _logger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: a Warning is logged for the lastJobCompletedAt fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenRunFallbackActiveJobIdUpdateFaults_LogsWarning()
    {
        // Arrange: agent is null (connection dropped), run fallback path — activeJobId Redis write fails.
        // The "run fallback path)" fragment is specific to the ContinueWith warning template and
        // distinct from the unconditional warning at the bottom of the else-if block, preventing
        // the TCS from being signalled by the wrong warning under parallel load.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        var fallbackAgentId = new AgentId(run.AgentId!);
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.UpdateAgentFieldAsync(fallbackAgentId, "activeJobId", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        // PostCompletionBookkeepingAsync also runs (run is non-null, non-consolidation) — set up its deps
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _logger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("run fallback path)")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        // Act: agent=null triggers the run-fallback path
        await _sut.HandleJobCompletedAsync(jobId, agent: null, MakePayload(), CancellationToken.None);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: a Warning is logged for the run-fallback activeJobId fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleJobCompletedAsync") && ctx.Contains("run fallback")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    // ── PermanentlyFailRejectedRunAsync label swap ────────────────────────

    [Fact]
    public async Task PermanentlyFailRejectedRunAsync_LabelSwapThrows_IsSwallowed()
    {
        // Characterization test: _labelService.SwapLabelAsync throwing must not propagate from
        // HandleJobRejectedAsync. After migration to TrySwapLabelAsync, the helper swallows
        // non-OCE exceptions.
        var agent = MakeAgent();
        var jobId = new JobId("job-rejected-1");
        var run = MakeRun("job-rejected-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.GetWorkItemRetryCountAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(99); // exhausted — goes to PermanentlyFailRejectedRunAsync
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.RemoveRun(jobId));

        // Label swap throws — must be swallowed
        _labelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var act = () => _sut.HandleJobRejectedAsync(jobId, agent, "test rejection", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── TrySwapLabelAfterOrphanedRecoveryAsync ────────────────────────────

    [Fact]
    public async Task TrySwapLabelAfterOrphanedRecovery_LabelSwapThrows_IsSwallowed()
    {
        // Characterization test: _labelService.SwapLabelAsync throwing must not propagate from
        // HandleOrphanedRunCompletedAsync. After migration to TrySwapLabelAsync the helper swallows
        // non-OCE exceptions.
        var jobId = new JobId("job-orphan-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#10",
                IssueProviderConfigId = "github-provider"
            });

        // Label swap throws — must be swallowed (cosmetic operation)
        _labelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        // Completed step triggers the label swap attempt
        var act = () => _sut.HandleJobCompletedAsync(jobId, agent: null,
            MakePayload(PipelineStep.Completed), CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Verify the swap was actually attempted (not vacuously swallowed by never reaching it)
        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TrySwapLabelAfterOrphanedRecovery_WhenRunRecordNull_NoSwapAttempted()
    {
        // When the work item record is null (WorkItem was fully deleted), no label swap should be attempted.
        var jobId = new JobId("job-orphan-2");

        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.TransitionWorkItemAsync(jobId, It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        await _sut.HandleJobCompletedAsync(jobId, agent: null,
            MakePayload(PipelineStep.Completed), CancellationToken.None);

        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never,
            "no label swap should be attempted when the work item run record is null");
    }

    // ── PostCompletionBookkeepingAsync — cancellation swallowing ─────────

    [Fact]
    public async Task HandleJobCompletedAsync_WhenBookkeepingCancelled_DoesNotThrow()
    {
        // Verifies that OperationCanceledException thrown inside PostCompletionBookkeepingAsync
        // (e.g. from SwapLabelAsync) is caught and does not propagate to the hub caller.
        // This is the cancellation-swallowing path noted as missing in the test TODO comment.
        var agent = MakeAgent();
        var jobId = new JobId("job-cancel-1");
        var run = MakeRun("job-cancel-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        // Simulate ApplicationStopping fire so the linked CTS propagates cancellation.
        using var appStoppingCts = new CancellationTokenSource();
        _appLifetime.Setup(l => l.ApplicationStopping).Returns(appStoppingCts.Token);
        // TODO: [WARNING] appStoppingCts.Cancel() is never called, so ApplicationStopping never
        // fires during this test. The test therefore only verifies that an OperationCanceledException
        // thrown by SwapLabelAsync mock is swallowed — it does NOT cover the actual graceful-shutdown
        // path where the linked CTS cancels the token passed to SwapLabelAsync. To test that path,
        // call appStoppingCts.Cancel() before the act and remove the mock that throws OCE from
        // SwapLabelAsync (the cancellation should originate from the token, not the mock).
        // (TestQualityReviewer review finding.)

        // SwapLabelAsync will throw OCE (simulates graceful-shutdown abort path)
        _issueOps
            .Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Re-build sut with non-default _appLifetime (needs new instance to pick up the new token)
        var sut = new AgentJobLifecycleService(
            new AgentJobLifecycleServiceDependencies(
                _facade.Object,
                _lifecycle.Object,
                _labelService.Object,
                _issueOps.Object,
                _changeNotifier.Object,
                _appLifetime.Object,
                _outbox.Object,
                _logger.Object));

        var act = () => sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);

        // PostCompletionBookkeepingAsync catches OCE and logs Information — must not propagate
        await act.Should().NotThrowAsync(
            "OperationCanceledException from bookkeeping must be caught inside PostCompletionBookkeepingAsync");
    }
}
