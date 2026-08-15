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

    // TODO: [WARNING] Mock Setup/Verify calls in this file use raw string literals (e.g., "job-1") for JobId
    // parameters, relying on the implicit string→JobId conversion. While this works at runtime via Moq's
    // value-equality matching (record struct equals compares .Value), consider updating to use
    // new JobId("job-1") for explicitness and to make the type constraint visible in tests.
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
        // TODO: [WARNING] GetWorkItemIssueMetadataAsync setup uses raw string "job-1" while the Verify
        // at line ~358 uses It.IsAny<JobId>(). Inconsistent style across this file; prefer new JobId("job-1")
        // in both Setup and Verify for clarity.
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

        // Metadata not available (non-DB mode or WorkItem not found) — MarkIssueComplete must not be called
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure (run not in memory)", FailureReason.AgentError), Times.Once);

        // Null metadata — MarkIssueComplete must not be called (no identifiers to pass)
        _facade.Verify(f => f.MarkIssueComplete(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()), Times.Never);
    }

    [Fact]
    public async Task Orphaned_cancelled_step_transitions_WorkItem_to_Cancelled_without_label_swap()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        // Metadata not available — MarkIssueComplete must not be called
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);

        // GetWorkItemIssueMetadataAsync IS called for Cancelled (all terminal statuses fetch metadata).
        // Label swap is only attempted on Succeeded, so _labelService is never called.
        _facade.Verify(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Once);
        _facade.Verify(f => f.MarkIssueComplete(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()), Times.Never);
    }

    [Fact]
    public async Task Orphaned_completed_step_calls_MarkIssueComplete_when_metadata_available()
    {
        // No run in memory (orphaned)
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("org/repo#1", "prov-1"));

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // MarkIssueComplete must be called with the identifiers from DB metadata
        _facade.Verify(f => f.MarkIssueComplete("org/repo#1", "prov-1"), Times.Once);

        // TODO: Also verify TransitionWorkItemAsync was called (WorkItemStatus.Succeeded) to guard the full
        // completion sequence — MarkIssueComplete must only be called *after* a successful WorkItem transition.
        // Without this assertion, a refactor that skips TransitionWorkItemAsync before MarkIssueComplete would
        // still produce a green test. (Correctness review warning, L372)
    }

    [Fact]
    public async Task Orphaned_completed_step_MarkIssueComplete_called_even_when_label_swap_throws()
    {
        // No run in memory (orphaned)
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("org/repo#1", "prov-1"));

        // Label swap throws (e.g., rate limit or network error)
        _labelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limit"));

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // MarkIssueComplete must be called before the label swap attempt — swap failure must not prevent it
        _facade.Verify(f => f.MarkIssueComplete("org/repo#1", "prov-1"), Times.Once);
    }

    [Fact]
    public async Task Orphaned_completed_step_MarkIssueComplete_not_called_when_metadata_unavailable()
    {
        // No run in memory (orphaned)
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        // DB not configured or WorkItem not found — metadata is null
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // Without identifiers we cannot call MarkIssueComplete — must remain unset
        _facade.Verify(f => f.MarkIssueComplete(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()), Times.Never);
    }

    // TODO: Add a test for FinalStep=Failed with metadata available that asserts MarkIssueComplete IS called.
    // The production code calls MarkIssueComplete for all terminal statuses (Succeeded, Failed, Cancelled)
    // when metadata is present, but only the Succeeded path has a positive-case test. A regression that gates
    // MarkIssueComplete behind workItemStatus == WorkItemStatus.Succeeded would not be detected by the current
    // test suite. (TestQualityReviewer warning, L430)

    // ── Post-completion bookkeeping contract ──────────────────────────────────

    [Fact]
    public async Task Regular_completed_step_calls_PostIssueFeedbackCommentAsync()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(i => i.PostIssueFeedbackCommentAsync(run), Times.Once);
    }

    [Fact]
    public async Task Consolidation_does_not_call_PostIssueFeedbackCommentAsync()
    {
        // Critical regression guard: after the strategy extraction, the bookkeeping skip
        // depends on the inline run-type check in HandleJobCompletedAsync. Without this test,
        // accidentally removing the guard would not be caught.
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(i => i.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()), Times.Never);
    }

    [Fact]
    public async Task Consolidation_does_not_call_SwapLabelAsync()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        // TODO: Add a test that exercises HandleJobCompletedAsync and HandleJobRejectedAsync with a Review-type run
        // (RunType == PipelineRunType.Review, LabelTargetKind == PullRequest). The refactor removed the explicit
        // LabelTargetKind argument; routing is now derived from run.LabelTargetKind. Without a Review-run test,
        // a regression in LabelTargetKind derivation for PR-type runs would go undetected here. (#2015)
        _issueOps.Verify(i => i.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }
}
