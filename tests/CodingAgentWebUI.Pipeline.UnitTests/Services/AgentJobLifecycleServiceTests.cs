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
            .ReturnsAsync(true);
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
            .ReturnsAsync(true);
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
            .ReturnsAsync(true);
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

    // ── HandleJobCompletedAsync — agent null fallback ─────────────────────

    [Fact]
    public async Task HandleJobCompletedAsync_AgentIsNull_AndRunHasAgentId_TransitionsAgentToIdle()
    {
        // Arrange: agent lookup returned null (connection dropped / hash expired),
        // but the run knows which agent was assigned.
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1"); // AgentId = "agent-1" from MakeRun

        // TODO: The mock setup for _lifecycle.CompleteRunAsync and _facade.ReplaceRun is missing.
        // Without it, CompleteRunAsync returns null (loose mock default), exercising an unintended
        // fallback path inside RegularJobCompletionStrategy. Add:
        //   _lifecycle.Setup(l => l.CompleteRunAsync(...)).ReturnsAsync(run);
        //   _facade.Setup(f => f.ReplaceRun(run));
        // to make this test accurately represent the production scenario.
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        var payload = MakePayload();

        // Act: agent is null — simulates registry lookup race
        await _sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

        // Assert: fallback path clears activeJobId and transitions to Idle using run's AgentId
        var expectedAgentId = new AgentId("agent-1");
        _facade.Verify(f => f.TransitionStatus(expectedAgentId, AgentStatus.Idle), Times.Once);
        _facade.Verify(f => f.UpdateAgentFieldAsync(expectedAgentId, "activeJobId", null), Times.Once);
        // TODO: Consider also asserting Times.Never for orphanRestoredAt and lastJobCompletedAt to
        // confirm the fallback branch — not the normal agent-is-not-null branch — was taken.
        // e.g.: _facade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), "orphanRestoredAt", It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_AgentIsNull_AndRunHasAgentId_LogsWarning()
    {
        // Arrange
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1"); // AgentId = "agent-1"

        // TODO: Same incomplete mock setup as the TransitionsAgentToIdle test — _lifecycle.CompleteRunAsync
        // and _facade.ReplaceRun are not set up, causing an unintended fallback inside the completion
        // strategy. See TODO in HandleJobCompletedAsync_AgentIsNull_AndRunHasAgentId_TransitionsAgentToIdle.
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        var payload = MakePayload();

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

        // Assert: a Warning is logged with the job ID and agent ID.
        // The log call is: _logger.Warning("{template}", jobId.Value /*string*/, run.AgentId /*string*/)
        // Compiler selects Warning<T0, T1>(string, T0, T1) — use typed matchers per brain entry dotnet.md#moq-serilog.
        // TODO: Tighten the argument matchers from It.IsAny<string>() to pinned values
        // (e.g. It.Is<string>(s => s == "job-1") and It.Is<string>(s => s == "agent-1")) so that
        // swapped or wrong argument values are caught by this assertion.
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
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

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
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string IssueIdentifier, string IssueProviderConfigId)?)null);

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
        var branchBefore = run.BranchName;
        AgentJobLifecycleService.ApplyStepMetadata(run, new() { ["UnknownKey"] = "whatever" });
        // State must be unchanged — unknown keys are silently ignored
        run.BranchName.Should().Be(branchBefore);
        run.RetryCount.Should().Be(0);
    }
}
