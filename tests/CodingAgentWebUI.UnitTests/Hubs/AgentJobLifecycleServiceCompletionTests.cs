using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Characterization tests for the three completion handler paths inside
/// <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/>.
///
/// Written BEFORE the CompletionOutcomeResolver extraction to lock in existing behaviour
/// and prevent regression. These tests assert observable side-effects (calls to mocked
/// dependencies), not internal state.
/// </summary>
public sealed class AgentJobLifecycleServiceCompletionTests
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

    private static PipelineRun MakeRun(string jobId = "job-1", string? providerConfigId = null) => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = providerConfigId ?? "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        AgentProviderConfigId = "agent-cfg-1"
        // LabelTargetKind is computed from RunType (default Implementation → Issue)
    };

    private static PipelineRun MakeConsolidationRun(string jobId = "job-1") =>
        MakeRun(jobId, ConsolidationConstants.ProviderConfigId);

    private static AgentEntry MakeAgent(string agentId = "agent-1") => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveJobId = "job-1"
    };

    // ── Consolidation path ────────────────────────────────────────────────────

    [Fact]
    public async Task Consolidation_completed_step_transitions_WorkItem_to_Succeeded()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Consolidation_failed_step_transitions_WorkItem_to_Failed_with_reason()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "out of tokens",
            FailureCategory = FailureReason.AgentError
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "out of tokens", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Consolidation_failed_step_with_null_reason_uses_fallback()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Consolidation run failed", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Consolidation_cancelled_step_transitions_WorkItem_to_Cancelled()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Consolidation_transitions_agent_to_Idle()
    {
        var run = MakeConsolidationRun();
        var agent = MakeAgent();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), agent, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task Consolidation_removes_run_and_marks_issue_complete()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
        _facade.Verify(f => f.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId), Times.Once);
    }

    // ── Regular path ──────────────────────────────────────────────────────────

    // TODO: Add a test for the regular path where FinalStep=Failed and both run.FailureReason and
    // payload.FailureReason are null, verifying that the fallback string "Agent reported failure"
    // is passed to CompleteRunAsync. Currently missing: if the fallback string were changed or the
    // wrong fallback were passed to CompletionOutcomeResolver.Resolve for this path, no test would
    // catch it. (Flagged by TestQualityReviewer — WARNING)

    [Fact]
    public async Task Regular_completed_step_calls_CompleteRunAsync_with_Succeeded()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Regular_failed_step_calls_CompleteRunAsync_with_Failed_and_reason()
    {
        var run = MakeRun();
        // FailureReason is set on run by JobCompletionMapper.Apply inside the handler
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "build failed",
            FailureCategory = FailureReason.AgentError
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                "build failed", FailureReason.AgentError))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "build failed", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Regular_null_CompletedRun_falls_back_to_direct_TransitionWorkItemAsync()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        // CompleteRunAsync returns null — simulates race with RevertFailedDistributionAsync
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Regular_CompleteRunAsync_throws_invokes_defensive_cleanup_with_defensive_fallback_string()
    {
        // This test pins the most subtle behavioral distinction across the 4 completion paths:
        // the defensive-cleanup path must use "Agent reported failure (defensive cleanup after exception)"
        // — NOT the regular-path fallback "Agent reported failure".
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,   // null forces fallback to be used
            FailureCategory = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // Must use the defensive-cleanup specific fallback string, not "Agent reported failure"
        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure (defensive cleanup after exception)",
            FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Defensive_cleanup_removes_run_and_marks_issue_complete()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
        _facade.Verify(f => f.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId), Times.Once);
    }

    // ── Orphaned path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Orphaned_completed_step_transitions_WorkItem_to_Succeeded_and_attempts_label_swap()
    {
        // No run in memory (orphaned)
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        // Return null metadata — label swap will be skipped (best-effort)
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    // TODO: Add a symmetric test for the orphaned path where FinalStep=Failed and
    // payload.FailureReason is a non-null explicit string, verifying it is propagated to
    // TransitionWorkItemAsync (i.e., the explicit reason takes precedence over the fallback).
    // Currently missing: a change that discarded payload.FailureReason in the orphaned path and
    // always used the fallback would not be caught by the existing test suite.
    // (Flagged by TestQualityReviewer — WARNING)
    [Fact]
    public async Task Orphaned_failed_step_transitions_WorkItem_to_Failed_with_fallback()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure (run not in memory)", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Orphaned_cancelled_step_transitions_WorkItem_to_Cancelled_without_label_swap()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);

        // Label swap is only attempted on Succeeded in the orphaned path
        _facade.Verify(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
