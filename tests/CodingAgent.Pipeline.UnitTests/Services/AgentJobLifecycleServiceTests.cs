using System.Linq;
using System.Threading;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

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
    private readonly Mock<IHostApplicationLifetime> _appLifetime = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentJobLifecycleService _sut;

    public AgentJobLifecycleServiceTests()
    {
        // ApplicationStopping is used by PostCompletionBookkeepingAsync to link cancellation tokens.
        // Provide a non-cancellable token so bookkeeping is not aborted in tests.
        _appLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        _sut = new AgentJobLifecycleService(
            _facade.Object,
            _lifecycle.Object,
            _labelService.Object,
            _issueOps.Object,
            _changeNotifier.Object,
            _appLifetime.Object,
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
        // TODO: [WARNING] This uses the 2-arg SwapLabelAsync overload (no CancellationToken). The
        // production call site is 3-arg: SwapLabelAsync(run, AgentLabels.Error, ct). Moq matches
        // overloads by parameter count, so this setup does NOT match the actual call and Moq returns
        // its default value, making the Verify below vacuously true. Change to:
        //   _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()))
        //       .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error)).Returns(Task.CompletedTask);

        await _sut.HandleJobRejectedAsync(jobId, agent, "crash", CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed,
            It.IsAny<CancellationToken>(), It.IsAny<string>(), FailureReason.InfrastructureFailure), Times.Once);
        // TODO: [WARNING] Same 2-arg vs 3-arg mismatch as the Setup above — this Verify will not
        // match the actual 3-arg call site and may give a false-positive result.
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
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

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
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

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
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Cancelled, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var payload = MakePayload(PipelineStep.Cancelled);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_FinalLabelOverridesTaken_WhenKnownLabel()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // FinalLabel = agent:error overrides Completed step
        var payload = MakePayload(finalLabel: AgentLabels.Error);

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_UnknownFinalLabel_IgnoredFallsBackToStep()
    {
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(run, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var payload = MakePayload(finalLabel: "custom:unknown");

        await _sut.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
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
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _issueOps.Verify(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Returns(Task.CompletedTask);

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
            .Returns(Task.CompletedTask);

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

    /// <summary>
    /// Waits deterministically for the fire-and-forget ContinueWith callback to have
    /// recorded at least one Warning invocation on <paramref name="loggerMock"/> whose
    /// message template contains <paramref name="templateFragment"/>. Uses SpinWait polling
    /// instead of Task.Delay so the barrier is tight (no fixed sleep) and not susceptible
    /// to thread-pool saturation on a loaded CI agent. Fails if the condition is not met
    /// within the timeout.
    /// </summary>
    private static void WaitForLoggerWarningContaining(Mock<ILogger> loggerMock, string templateFragment,
        int timeoutMs = 5000)
    {
        // TODO: [WARNING] SpinWait.SpinUntil polls the Moq invocation list, which requires the
        // continuation to have been scheduled and executed on the thread pool. This is more reliable
        // than a fixed Task.Delay, but the correct long-term fix is to expose the ContinueWith task
        // from the production code so tests can await it directly without any polling.
        // TODO: [WARNING] loggerMock.Invocations is accessed from the test thread while the
        // ContinueWith callback writes to it from a thread-pool thread. Moq's InvocationCollection
        // uses an internal lock, but this is an undocumented implementation detail. Under load a
        // SpinWait iteration can observe a torn read: HasMatchingWarning() returns true after the
        // first continuation fires, SpinWait exits, a second continuation then fires and increments
        // the count, and Times.Once fails. The fix is to return the continuation task from the
        // production method so tests can await it directly, eliminating all polling races.
        bool HasMatchingWarning() =>
            loggerMock.Invocations.Any(i =>
                i.Method.Name == nameof(ILogger.Warning) &&
                i.Arguments.OfType<string>().Any(s => s.Contains(templateFragment)));

        SpinWait.SpinUntil(HasMatchingWarning, timeoutMs);

        if (!HasMatchingWarning())
            throw new TimeoutException(
                $"Timed out after {timeoutMs}ms waiting for a Warning invocation containing '{templateFragment}' " +
                $"on the logger mock. Warning invocations recorded: {loggerMock.Invocations.Count(i => i.Method.Name == nameof(ILogger.Warning))}");
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenUpdateAgentFieldFaults_LogsWarning()
    {
        // Arrange: the activeJobId Redis write fails
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        // TODO: [WARNING] The "lastJobCompletedAt" call is not configured here, so Moq returns its
        // default. If Moq's default for a Task-returning method is null (not Task.CompletedTask),
        // the production .ContinueWith() call on that null reference throws NullReferenceException
        // synchronously inside HandleJobRejectedAsync before the assertion is reached. Add:
        //   _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()))
        //       .Returns(Task.CompletedTask);
        // to make the isolation explicit and guard against Moq version differences.

        // Act
        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);

        // Wait deterministically for the thread-pool ContinueWith callback to fire.
        // Task.FromException produces an already-faulted task so the continuation is queued
        // to the thread pool immediately after the fire-and-forget discard. SpinWait polls
        // the mock's Invocations list until the Warning is recorded, avoiding the fixed
        // 2-second sleep that was susceptible to thread-pool saturation on loaded CI agents.
        WaitForLoggerWarningContaining(_logger, "HandleJobRejectedAsync");

        // Assert: a Warning is logged with the exception, method context, AgentId, and field name.
        // Serilog's Warning<T0,T1>(Exception?, string, T0, T1) overload is selected by the compiler
        // when two typed structural params are present (agent.AgentId = AgentId, field = string).
        // Moq resolves generic overloads by concrete type — It.IsAny<object>() would NOT match here.
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                // TODO: [WARNING] Checking for literal Serilog template tokens ("{AgentId}", "{Field}")
                // couples the assertion to message-template syntax rather than observable behaviour.
                // Consider asserting on the rendered AgentId value and field name string instead,
                // so the test survives message-template refactors that preserve the structured data.
                It.Is<string>(s => s.Contains("HandleJobRejectedAsync") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.IsAny<AgentId>(),    // T0 = AgentId
                It.IsAny<string>()),    // T1 = string (field name)
            Times.Once);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_WhenLastJobCompletedAtUpdateFaults_LogsWarning()
    {
        // Arrange: the lastJobCompletedAt Redis write fails.
        // activeJobId is not configured to fault here so only one Warning fires.
        // TODO: [WARNING] Times.Once depends on Moq returning Task.CompletedTask by default for
        // the activeJobId call. If the mock default changes, this assertion becomes fragile.
        // Explicitly configure UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null) to return
        // Task.CompletedTask to make the isolation intentional rather than implicit.
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns((PipelineRun?)null);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        // Act
        await _sut.HandleJobRejectedAsync(jobId, agent, "reason", CancellationToken.None);
        WaitForLoggerWarningContaining(_logger, "HandleJobRejectedAsync");

        // Assert: a Warning is logged for the lastJobCompletedAt fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("HandleJobRejectedAsync") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenActiveJobIdUpdateFaults_LogsWarning()
    {
        // Arrange: the activeJobId Redis write fails in HandleJobCompletedAsync (agent path).
        // orphanRestoredAt and lastJobCompletedAt are not configured to fault, so only one Warning fires.
        // TODO: [WARNING] Times.Once relies on the Moq default (Task.CompletedTask) for the other two
        // field writes. Tighten the It.Is<string> predicate to also check s.Contains("activeJobId")
        // to ensure the correct fault path is verified even if other continuations fire.
        // TODO: [WARNING] SwapLabelAsync is set up with It.IsAny<CancellationToken>(). Verify this
        // matches the actual overload called by HandleJobCompletedAsync (3-arg with CancellationToken).
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);
        WaitForLoggerWarningContaining(_logger, "HandleJobCompletedAsync");

        // Assert: a Warning is logged for the activeJobId fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("HandleJobCompletedAsync") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenOrphanRestoredAtUpdateFaults_LogsWarning()
    {
        // Arrange: the orphanRestoredAt Redis write fails in HandleJobCompletedAsync (agent path).
        // activeJobId and lastJobCompletedAt are not configured to fault.
        // TODO: [WARNING] Times.Once relies on the Moq default for the other two field writes.
        // Tighten the It.Is<string> predicate to also check s.Contains("orphanRestoredAt").
        // TODO: [WARNING] SwapLabelAsync is set up with It.IsAny<CancellationToken>(). Verify this
        // matches the actual overload called by HandleJobCompletedAsync (3-arg with CancellationToken).
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);
        WaitForLoggerWarningContaining(_logger, "HandleJobCompletedAsync");

        // Assert: a Warning is logged for the orphanRestoredAt fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("HandleJobCompletedAsync") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenLastJobCompletedAtUpdateFaults_LogsWarning()
    {
        // Arrange: the lastJobCompletedAt Redis write fails in HandleJobCompletedAsync (agent path).
        // activeJobId and orphanRestoredAt are not configured to fault.
        // TODO: [WARNING] Times.Once relies on the Moq default for the other two field writes.
        // Tighten the It.Is<string> predicate to also check s.Contains("lastJobCompletedAt").
        // TODO: [WARNING] SwapLabelAsync is set up with It.IsAny<CancellationToken>(). Verify this
        // matches the actual overload called by HandleJobCompletedAsync (3-arg with CancellationToken).
        var agent = MakeAgent();
        var jobId = new JobId("job-1");
        var run = MakeRun("job-1");
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _facade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        // Act
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);
        WaitForLoggerWarningContaining(_logger, "HandleJobCompletedAsync");

        // Assert: a Warning is logged for the lastJobCompletedAt fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("HandleJobCompletedAsync") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WhenRunFallbackActiveJobIdUpdateFaults_LogsWarning()
    {
        // Arrange: agent is null (connection dropped), run fallback path — activeJobId Redis write fails
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
            .Returns(Task.CompletedTask);

        // Act: agent=null triggers the run-fallback path
        await _sut.HandleJobCompletedAsync(jobId, agent: null, MakePayload(), CancellationToken.None);
        WaitForLoggerWarningContaining(_logger, "run fallback");

        // Assert: a Warning is logged for the run-fallback activeJobId fault path
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("HandleJobCompletedAsync") && s.Contains("{AgentId}") && s.Contains("{Field}") && s.Contains("run fallback")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.Once);
    }
}
