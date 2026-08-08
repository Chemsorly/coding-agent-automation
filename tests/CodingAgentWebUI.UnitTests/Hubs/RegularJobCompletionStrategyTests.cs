using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="RegularJobCompletionStrategy"/> in isolation.
/// Tests verify the regular completion path independently of consolidation logic.
/// </summary>
public sealed class RegularJobCompletionStrategyTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycleManager = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();

    private RegularJobCompletionStrategy CreateStrategy() => new(
        _facade.Object,
        _lifecycleManager.Object,
        _changeNotifier.Object,
        _logger.Object);

    private static PipelineRun MakeRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        AgentProviderConfigId = "agent-cfg-1"
    };

    // ── Status routing ────────────────────────────────────────────────────────

    [Fact]
    public async Task Completed_step_calls_CompleteRunAsync_with_Succeeded()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Failed_step_with_explicit_reason_calls_CompleteRunAsync_with_that_reason()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "build failed",
            FailureCategory = FailureReason.AgentError
        };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                "build failed", FailureReason.AgentError))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "build failed", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Failed_step_with_null_reason_uses_agent_reported_failure_fallback()
    {
        // Fills the gap identified in prior review: no test pinned the "Agent reported failure" fallback string.
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };

        // TODO: The Setup matches on the exact fallback string "Agent reported failure". If CompleteRunAsync
        // is called with a different string the Setup won't match, returning the default null, which silently
        // exercises the race-condition fallback path instead of failing the test. Use It.IsAny<string?>() for
        // the Setup and capture/assert the actual argument separately (e.g. via a Callback), or additionally
        // assert that TransitionWorkItemAsync was NOT called directly to confirm the normal path was taken.
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                "Agent reported failure", FailureReason.AgentError))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure", FailureReason.AgentError), Times.Once);
    }

    // ── Race condition (null CompleteRunAsync result) ─────────────────────────

    [Fact]
    public async Task Null_CompleteRunAsync_result_falls_back_to_direct_TransitionWorkItemAsync()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        // CompleteRunAsync returns null — simulates race with RevertFailedDistributionAsync
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    // ── Defensive cleanup path ────────────────────────────────────────────────

    [Fact]
    public async Task CompleteRunAsync_throws_invokes_defensive_cleanup_with_defensive_fallback_string()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,   // null forces fallback to be used
            FailureCategory = null
        };

        // TODO: FailureReason = null means JobCompletionMapper.Apply copies null to run.FailureReason.
        // The defensive-cleanup path calls CompletionOutcomeResolver.Resolve again with run.FailureReason
        // (null), producing "Agent reported failure (defensive cleanup after exception)" from the null
        // branch — but the normal resolve path also produces a null-based fallback. This test cannot
        // distinguish the two paths. Use a non-null FailureReason in the payload so the normal path
        // would produce that reason, while the defensive path independently hits its own fallback string.
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        // Must use the defensive-cleanup specific fallback, not "Agent reported failure"
        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure (defensive cleanup after exception)",
            FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_throws_removes_run_and_marks_issue_complete()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
        _facade.Verify(f => f.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId), Times.Once);
        // TODO: No assertion that TransitionWorkItemAsync is called with WorkItemStatus.Succeeded during
        // the defensive path. A change that drops or mis-routes that call would not be caught here.
        // Add: _facade.Verify(f => f.TransitionWorkItemAsync("job-1", WorkItemStatus.Succeeded, ...), Times.Once)
    }

    // ── JobCompletionMapper ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_applies_JobCompletionMapper_to_run()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/org/repo/pull/99"
        };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        // Verify JobCompletionMapper.Apply was called by checking the observable side-effect
        run.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/99");
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_calls_NotifyChange()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ── Agent isolation ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_does_not_call_TransitionStatus()
    {
        // Strategy must not touch agent state — that is the caller's responsibility
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }
}
