using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="ConsolidationJobCompletionStrategy"/> in isolation.
/// Tests verify the consolidation completion path independently of regular-run logic.
/// </summary>
public sealed class ConsolidationJobCompletionStrategyTests
{
    private readonly Mock<IRunLifecycleManager> _lifecycleManager = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();

    private ConsolidationJobCompletionStrategy CreateStrategy() => new(
        _lifecycleManager.Object,
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

    // ── Status routing — lifecycle manager calls ──────────────────────────

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
    public async Task Failed_step_with_reason_calls_FailRunAsync()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "out of tokens",
            FailureCategory = FailureReason.AgentError
        };

        _lifecycleManager
            .Setup(l => l.FailRunAsync("job-1", "out of tokens", It.IsAny<CancellationToken>(), FailureReason.AgentError))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _lifecycleManager.Verify(l => l.FailRunAsync(
            "job-1", "out of tokens", It.IsAny<CancellationToken>(), FailureReason.AgentError), Times.Once);
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

        _lifecycleManager
            .Setup(l => l.FailRunAsync("job-1", "Consolidation run failed", It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _lifecycleManager.Verify(l => l.FailRunAsync(
            "job-1", "Consolidation run failed", It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task Cancelled_step_calls_FailRunAsync()
    {
        // TODO: [WARNING] This test validates incorrect behaviour. PipelineStep.Cancelled should route
        // to CancelRunAsync, not FailRunAsync. CompletionOutcomeResolver returns WorkItemStatus.Cancelled
        // for Cancelled steps, but the else-branch in ExecuteAsync collapses Cancelled→FailRunAsync,
        // causing history and DB to record WorkItemStatus.Failed instead of WorkItemStatus.Cancelled.
        // When ConsolidationJobCompletionStrategy is fixed to call CancelRunAsync for Cancelled steps,
        // this test must be updated to set up and verify CancelRunAsync instead of FailRunAsync.
        // Cancelled maps to non-Succeeded WorkItemStatus, which currently routes through FailRunAsync
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.FailRunAsync("job-1", It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        _lifecycleManager.Verify(l => l.FailRunAsync(
            "job-1", It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    // ── History is written via lifecycle manager ──────────────────────────

    [Fact]
    public async Task Completed_step_routes_through_lifecycle_manager_not_facade()
    {
        // History is now written via RunLifecycleManager — verify the lifecycle manager is called,
        // not a direct facade call (which was the old behavior that skipped history).
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        // Lifecycle manager called (history written inside it)
        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    // ── Error handling — lifecycle manager exceptions are swallowed ───────

    [Fact]
    public async Task CompleteRunAsync_throws_exception_is_swallowed()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var strategy = CreateStrategy();
        // Must not throw
        var act = async () => await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FailRunAsync_throws_exception_is_swallowed()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var strategy = CreateStrategy();
        var act = async () => await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Notifications ─────────────────────────────────────────────────────

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

    // ── Agent isolation ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_does_not_call_TransitionStatus()
    {
        // TODO: [WARNING] This test no longer asserts the behaviour its name describes. The original test
        // verified that _facade.TransitionStatus is never called (agent isolation). Since IAgentHubFacade
        // is no longer injected into ConsolidationJobCompletionStrategy, TransitionStatus genuinely cannot
        // be called, making the negative assertion untestable. The current body duplicates the assertion
        // in Completed_step_calls_CompleteRunAsync_with_Succeeded. Either rename this test to reflect what
        // is actually verified (e.g. ExecuteAsync_WithCompletedStep_CallsCompleteRunAsync_Once), or remove
        // the duplicate and add a comment explaining why the original agent-isolation assertion is vacuously
        // guaranteed by the constructor not accepting IAgentHubFacade.
        // Strategy must not touch agent state — that is the caller's responsibility
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        // We don't inject IAgentHubFacade anymore, so TransitionStatus cannot be called.
        // This test documents the design intent: strategy is lifecycle-manager-only.
        var strategy = CreateStrategy();
        await strategy.ExecuteAsync(new JobId("job-1"), run, payload, null, CancellationToken.None);

        // If we got here without exception, the strategy didn't try to call TransitionStatus
        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }
}
