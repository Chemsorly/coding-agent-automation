using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="ConsolidationJobCompletionStrategy"/> in isolation.
/// Tests verify the consolidation completion path independently of regular-run logic.
/// </summary>
public sealed class ConsolidationJobCompletionStrategyTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();

    private ConsolidationJobCompletionStrategy CreateStrategy() => new(
        _facade.Object,
        _changeNotifier.Object,
        _logger.Object);

    private static PipelineRun MakeRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#consolidation",
        IssueTitle = "Consolidation Run",
        IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
        RepoProviderConfigId = "repo-cfg-1",
        AgentProviderConfigId = "agent-cfg-1"
    };

    // ── Status routing ────────────────────────────────────────────────────────

    [Fact]
    public async Task Completed_step_transitions_WorkItem_to_Succeeded()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Failed_step_with_reason_transitions_WorkItem_to_Failed_with_that_reason()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "out of tokens",
            FailureCategory = FailureReason.AgentError
        };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "out of tokens", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Failed_step_with_null_reason_uses_consolidation_run_failed_fallback()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Consolidation run failed", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Cancelled_step_transitions_WorkItem_to_Cancelled()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    // ── Run cleanup ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Completed_step_removes_run_and_marks_issue_complete()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
        _facade.Verify(f => f.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId), Times.Once);
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionWorkItemAsync_throws_exception_is_swallowed()
    {
        // The consolidation path wraps TransitionWorkItemAsync in try/catch — exception must not propagate
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade
            .Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var strategy = CreateStrategy();
        // Must not throw
        var act = async () => await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_calls_NotifyChange()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _changeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ── Pipeline history persistence is skipped ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_does_not_call_AddRunToHistoryAsync()
    {
        // Consolidation runs skip pipeline history persistence intentionally
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Agent isolation ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_does_not_call_TransitionStatus()
    {
        // Strategy must not touch agent state — that is the caller's responsibility
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }
}
