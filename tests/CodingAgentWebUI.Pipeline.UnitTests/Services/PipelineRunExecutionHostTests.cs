using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelineRunExecutionHost"/>.
/// Verifies the try/OCE-catch/generic-catch → discriminated outcome routing.
/// </summary>
public class PipelineRunExecutionHostTests
{
    [Fact]
    public async Task ExecuteStepsAsync_StepsComplete_ReturnsCompletedOutcome()
    {
        var (steps, context) = BuildSuccessfulPipeline();

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync(steps, context, CancellationToken.None);

        outcome.Should().BeOfType<PipelineExecutionOutcome.CompletedOutcome>();
        outcome.Run.Should().BeSameAs(context.Run);
    }

    [Fact]
    public async Task ExecuteStepsAsync_StepReturnsStop_ReturnsCompletedOutcome()
    {
        var run = CreateRun();
        var step = new FakeStep(StepResult.Stop);
        var context = BuildContext(run);

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync([step], context, CancellationToken.None);

        outcome.Should().BeOfType<PipelineExecutionOutcome.CompletedOutcome>();
        outcome.Run.Should().BeSameAs(run);
    }

    [Fact]
    public async Task ExecuteStepsAsync_OperationCanceled_ReturnsCancelledOutcome()
    {
        var run = CreateRun();
        var step = new ThrowingStep(new OperationCanceledException());
        var context = BuildContext(run);

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync([step], context, CancellationToken.None);

        outcome.Should().BeOfType<PipelineExecutionOutcome.CancelledOutcome>();
        outcome.Run.Should().BeSameAs(run);
    }

    [Fact]
    public async Task ExecuteStepsAsync_TaskCanceledException_ReturnsCancelledOutcome()
    {
        var run = CreateRun();
        var step = new ThrowingStep(new TaskCanceledException());
        var context = BuildContext(run);

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync([step], context, CancellationToken.None);

        // TaskCanceledException derives from OperationCanceledException
        outcome.Should().BeOfType<PipelineExecutionOutcome.CancelledOutcome>();
    }

    [Fact]
    public async Task ExecuteStepsAsync_GenericException_ReturnsFailedOutcomeWithException()
    {
        var run = CreateRun();
        var expectedException = new InvalidOperationException("something broke");
        var step = new ThrowingStep(expectedException);
        var context = BuildContext(run);

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync([step], context, CancellationToken.None);

        var failed = outcome.Should().BeOfType<PipelineExecutionOutcome.FailedOutcome>().Which;
        failed.Run.Should().BeSameAs(run);
        failed.Exception.Should().BeSameAs(expectedException);
    }

    [Fact]
    public async Task ExecuteStepsAsync_MultipleSteps_ExecutesInOrder()
    {
        var executionOrder = new List<int>();
        var run = CreateRun();
        var steps = new IPipelineStep[]
        {
            new TrackingStep(1, executionOrder),
            new TrackingStep(2, executionOrder),
            new TrackingStep(3, executionOrder),
        };
        var context = BuildContext(run);

        await PipelineRunExecutionHost.ExecuteStepsAsync(steps, context, CancellationToken.None);

        executionOrder.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ExecuteStepsAsync_CancellationTokenCancelled_ReturnsCancelledOutcome()
    {
        var run = CreateRun();
        using var cts = new CancellationTokenSource();
        // Step that cancels the token, then throws OCE on next check
        var step = new CancellingStep(cts);
        var context = BuildContext(run);

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync([step], context, cts.Token);

        outcome.Should().BeOfType<PipelineExecutionOutcome.CancelledOutcome>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => PipelineRun.Create(
        runId: "test-run-1",
        issueIdentifier: "org/repo#1",
        issueTitle: "Test Issue",
        issueProviderConfigId: "issue-1",
        repoProviderConfigId: "repo-1",
        initiatedBy: "test");

    private static PipelineStepContext BuildContext(PipelineRun run) => new()
    {
        Run = run,
        Config = new PipelineConfiguration(),
        RepoProvider = Mock.Of<IRepositoryProvider>(),
        AgentProvider = Mock.Of<IAgentProvider>(),
        BrainProvider = null,
        PipelineProvider = null,
        Cts = new CancellationTokenSource(),
        ConfigStore = Mock.Of<IConfigurationStore>(),
        Callbacks = Mock.Of<IPipelineCallbacks>(),
        IssueOps = Mock.Of<IAgentIssueOperations>(),
        AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
        QualityGates = Mock.Of<IQualityGateExecutor>(),
        BrainSync = null,
        PrOrchestrator = new PullRequestOrchestrator(Mock.Of<Serilog.ILogger>()),
        Logger = Mock.Of<Serilog.ILogger>(),
    };

    private static (IReadOnlyList<IPipelineStep> Steps, PipelineStepContext Context) BuildSuccessfulPipeline()
    {
        var run = CreateRun();
        var step = new FakeStep(StepResult.Continue);
        return ([step], BuildContext(run));
    }

    /// <summary>A step that always returns the configured result.</summary>
    private sealed class FakeStep(StepResult result) : IPipelineStep
    {
        public string StepName => "FakeStep";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct) =>
            Task.FromResult(result);
    }

    /// <summary>A step that always throws the configured exception.</summary>
    private sealed class ThrowingStep(Exception exception) : IPipelineStep
    {
        public string StepName => "ThrowingStep";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct) =>
            throw exception;
    }

    /// <summary>A step that records its execution order.</summary>
    private sealed class TrackingStep(int order, List<int> tracker) : IPipelineStep
    {
        public string StepName => $"TrackingStep_{order}";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
        {
            tracker.Add(order);
            return Task.FromResult(StepResult.Continue);
        }
    }

    /// <summary>A step that cancels the CTS and then throws OCE.</summary>
    private sealed class CancellingStep(CancellationTokenSource cts) : IPipelineStep
    {
        public string StepName => "CancellingStep";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(StepResult.Continue);
        }
    }
}
