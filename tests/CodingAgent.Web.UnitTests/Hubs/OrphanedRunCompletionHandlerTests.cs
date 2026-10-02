using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Direct unit tests for <see cref="OrphanedRunCompletionHandler"/>.
/// These tests cover the handler's own branching logic — WorkItem status transitions,
/// label routing (task-type defaults, FinalLabel overrides, Review vs non-Review targets),
/// and the null-RepoProviderConfigId fallback for Review runs.
/// </summary>
public sealed class OrphanedRunCompletionHandlerTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<ILogger> _logger = new();

    private OrphanedRunCompletionHandler CreateHandler() =>
        new(_facade.Object, _labelService.Object, _logger.Object);

    private static JobCompletionPayload MakePayload(
        PipelineStep step,
        string? finalLabel = null,
        FailureReason? failureCategory = null,
        string? failureReason = null) =>
        new()
        {
            FinalStep = step,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = finalLabel,
            FailureCategory = failureCategory,
            FailureReason = failureReason
        };

    // ── TransitionWorkItemAsync is always called ────────────────────────────

    [Fact]
    public async Task HandleAsync_CompletedStep_TransitionsWorkItemToSucceeded()
    {
        // TODO: [WARNING] TransitionWorkItemAsync is not set up with ReturnsAsync(true) here
        // (Moq default returns Task<bool> with value false). The production code ignores the
        // return value, so this does not cause failures today. If future production code branches
        // on the return value, all tests without explicit setup will silently exercise the false
        // path. Add _facade.Setup(...TransitionWorkItemAsync...).ReturnsAsync(true) for success-path
        // tests to make intent explicit. (DotNetSpecialist / TestQualityReviewer WARNING)
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Completed), CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_FailedStep_TransitionsWorkItemToFailed()
    {
        // TODO: [WARNING] This test uses It.IsAny<string?>() and It.IsAny<FailureReason?>() for
        // the error message and failure category, so it does not pin the resolved default values
        // from CompletionOutcomeResolver.Resolve for a plain Failed step (no explicit reason).
        // A regression changing the default failure message or enum mapping would not be caught.
        // Add a companion assertion using exact values for the default resolution (null error
        // message, null FailureReason) to cover the no-explicit-reason case.
        // The explicit-reason case is covered by HandleAsync_FailedStep_WithExplicitReason_PropagatesReasonToTransition.
        // (TestQualityReviewer WARNING)
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Failed), CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_CancelledStep_TransitionsWorkItemToCancelled()
    {
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Cancelled), CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    // ── Label swap: Succeeded path only ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_CompletedStep_WithRunRecord_SwapsLabelToDone()
    {
        // Arrange
        // TODO: [WARNING] _labelService.SwapLabelAsync is not explicitly set up here (Moq default
        // returns Task). The Verify below will pass regardless of whether the mock is configured,
        // because Moq's strict mode is not used. If ILabelService.SwapLabelAsync is later changed
        // to a non-virtual default implementation or the TrySwapLabelAsync extension routing changes,
        // the test could silently pass without the swap actually firing. Add an explicit
        // _labelService.Setup(l => l.SwapLabelAsync(...)).Returns(Task.CompletedTask) to make the
        // intent clear and guard against silent mock-pass scenarios. (DotNetSpecialist WARNING)
        // TODO: [WARNING] This test verifies that SwapLabelAsync targets LabelTargetKind.Issue (Times.Once)
        // but does not add a complementary Times.Never assertion for LabelTargetKind.PullRequest.
        // A bug causing TrySwapLabelAfterOrphanedRecoveryAsync to additionally fire the PR path
        // would pass this test. Add a _labelService.Verify(...LabelTargetKind.PullRequest..., Times.Never)
        // to close the gap — mirroring the HandleAsync_CompletedStep_ReviewRun_SwapsLabelToNext_OnPullRequest
        // test which correctly includes a Times.Never for LabelTargetKind.Issue. (TestQualityReviewer WARNING)
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#1",
                IssueProviderConfigId = "ipc-1"
            });

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Completed), CancellationToken.None);

        // Assert: label swap fires for Succeeded path
        _labelService.Verify(l => l.SwapLabelAsync(
            "ipc-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_CancelledStep_NoLabelSwap()
    {
        // Label swap only fires on Succeeded; cancelled path must not touch labels.
        // TODO: [WARNING] The guard that prevents the label swap fires on workItemStatus
        // (WorkItemStatus.Succeeded), not directly on FinalStep. CompletionOutcomeResolver.Resolve
        // is a static class whose Cancelled→WorkItemStatus.Cancelled mapping is not controlled by
        // this test. If a future refactor accidentally maps Cancelled→Succeeded, the test would
        // fail at the TransitionWorkItemAsync verify before reaching the SwapLabelAsync Never-verify,
        // masking the label-swap regression. A companion test using a Failed step (which maps to
        // WorkItemStatus.Failed, confirmed non-Succeeded) would make the guard coverage more complete.
        // (TestQualityReviewer WARNING)
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#2",
                IssueProviderConfigId = "ipc-2"
            });

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Cancelled), CancellationToken.None);

        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never,
            "cancelled path must not trigger a label swap");
    }

    [Fact]
    public async Task HandleAsync_NullRunRecord_SkipsLabelSwap()
    {
        // When GetWorkItemRunRecordAsync returns null, TrySwapLabelAfterOrphanedRecoveryAsync is a no-op.
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Completed), CancellationToken.None);

        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never,
            "null runRecord must suppress the label swap entirely");
    }

    // ── Explicit failure reason is propagated ───────────────────────────────

    [Fact]
    public async Task HandleAsync_FailedStep_WithExplicitReason_PropagatesReasonToTransition()
    {
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var sut = CreateHandler();
        await sut.HandleAsync(
            new JobId("job-1"),
            MakePayload(PipelineStep.Failed, failureReason: "out of tokens", failureCategory: FailureReason.AgentError),
            CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "out of tokens", FailureReason.AgentError), Times.Once);
    }

    // ── Label routing: Review task type ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_CompletedStep_ReviewRun_SwapsLabelToNext_OnPullRequest()
    {
        // Review run completion: label must be agent:next, target must be PullRequest,
        // and the provider config must be RepoProviderConfigId (not IssueProviderConfigId).
        // TODO: [WARNING] This test asserts AgentLabels.Next for a Review completion. The production
        // code implements agent:next for Review runs on the orphan-recovery path, but this diverges
        // from the normal completion path (SwapLabelAndPostCommentAsync in AgentJobLifecycleService),
        // which applies agent:done unconditionally for all task types including Review when
        // FinalStep == Completed and no FinalLabel is set. This assertion therefore locks in a known
        // inconsistency between the orphan path and the normal path. If the divergence is intentional,
        // document it (e.g. rename the test to include "OrphanPath" and add a comment explaining the
        // semantic difference). If it is a bug, fix OrphanedRunCompletionHandler to match the normal
        // path and update this assertion to AgentLabels.Done. See in-code TODO in
        // OrphanedRunCompletionHandler.TrySwapLabelAfterOrphanedRecoveryAsync. (DotNetSpecialist WARNING)
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Review,
                IssueIdentifier = "pr-5",
                IssueProviderConfigId = "issue-prov",
                RepoProviderConfigId = "repo-prov"
            });

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Completed), CancellationToken.None);

        // Must use RepoProviderConfigId and target the PR.
        _labelService.Verify(l => l.SwapLabelAsync(
            "repo-prov", "pr-5",
            AgentLabels.Next, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once,
            "Review completion must target the pull request via RepoProviderConfigId");

        // Must NOT have targeted the issue.
        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Never,
            "Review completion must not target the issue");
    }

    [Fact]
    public async Task HandleAsync_CompletedStep_ReviewRun_NullRepoProviderConfigId_FallsBackToIssueProvider()
    {
        // When a Review run has no RepoProviderConfigId, the handler falls back to
        // IssueProviderConfigId (still targeting PullRequest) and logs a warning.
        // TODO: [WARNING] This fallback is documented as potentially incorrect when issues and PRs
        // are hosted on different providers (e.g. Jira + GitHub). The safer fix is to skip the swap
        // entirely. See OrphanedRunCompletionHandler.TrySwapLabelAfterOrphanedRecoveryAsync.
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Review,
                IssueIdentifier = "pr-7",
                IssueProviderConfigId = "issue-prov",
                RepoProviderConfigId = null  // triggers the fallback branch
            });

        var sut = CreateHandler();
        await sut.HandleAsync(new JobId("job-1"), MakePayload(PipelineStep.Completed), CancellationToken.None);

        // Fallback: IssueProviderConfigId is used, but target is still PullRequest.
        _labelService.Verify(l => l.SwapLabelAsync(
            "issue-prov", "pr-7",
            AgentLabels.Next, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once,
            "null RepoProviderConfigId must fall back to IssueProviderConfigId, still targeting PullRequest");
    }

    // ── FinalLabel override ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_CompletedStep_WithKnownFinalLabel_UsesFinalLabelInsteadOfDefault()
    {
        // When payload.FinalLabel is a known AgentLabels.All member (e.g. agent:epic-review),
        // it overrides the step-based default (agent:done for Implementation/Completed).
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "issue-42",
                IssueProviderConfigId = "ipc-1"
            });

        var sut = CreateHandler();
        await sut.HandleAsync(
            new JobId("job-1"),
            MakePayload(PipelineStep.Completed, finalLabel: AgentLabels.EpicReview),
            CancellationToken.None);

        // FinalLabel takes precedence — must not apply agent:done.
        _labelService.Verify(l => l.SwapLabelAsync(
            "ipc-1", "issue-42",
            AgentLabels.EpicReview, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once,
            "known FinalLabel must override the step-based default");

        _labelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            AgentLabels.Done, It.IsAny<LabelTargetKind>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "step-based default (agent:done) must not be applied when FinalLabel is set");
    }

    [Fact]
    public async Task HandleAsync_CompletedStep_WithUnknownFinalLabel_FallsBackToStepDefault()
    {
        // When payload.FinalLabel is not in AgentLabels.All, the step-based default is used.
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(new JobId("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "issue-42",
                IssueProviderConfigId = "ipc-1"
            });

        var sut = CreateHandler();
        await sut.HandleAsync(
            new JobId("job-1"),
            MakePayload(PipelineStep.Completed, finalLabel: "agent:unknown-label-xyz"),
            CancellationToken.None);

        // Unknown FinalLabel is ignored; step default (Completed → agent:done for Implementation) applies.
        _labelService.Verify(l => l.SwapLabelAsync(
            "ipc-1", "issue-42",
            AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once,
            "unknown FinalLabel must be ignored and the step-based default applied");
    }
}
