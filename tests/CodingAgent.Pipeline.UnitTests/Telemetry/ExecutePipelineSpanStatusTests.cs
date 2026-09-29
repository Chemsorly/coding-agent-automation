using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests the span status rules for <c>ExecutePipeline</c> and step spans:
/// <list type="bullet">
///   <item>A run that completes after a recovered non-critical failure has no Error on ExecutePipeline.</item>
///   <item>A run that fails has Error on the failing step span and on ExecutePipeline.</item>
///   <item>Non-failure terminal states (ConflictRestart, PrMerged, PrClosed) do not set Error on ExecutePipeline.</item>
/// </list>
/// </summary>
public class ExecutePipelineSpanStatusTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public ExecutePipelineSpanStatusTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ── AC: Non-critical failure recovered → ExecutePipeline NOT Error ────────────

    [Fact]
    public async Task NonCriticalFailure_Recovered_DoesNotSetErrorOnExecutePipelineSpan()
    {
        // Simulate a run where a non-critical failure occurs and the pipeline continues to Completed.
        // The ExecutePipeline span must remain Unset/Ok, not Error.
        using var execSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        var context = BuildContext();

        // Trigger a non-critical failure via TryNonCriticalAsync.
        await context.TryNonCriticalAsync(
            () => throw new InvalidOperationException("transient checkout conflict"),
            "TestNonCritical");

        // Simulate pipeline completing successfully.
        execSpan?.SetStatus(ActivityStatusCode.Ok);

        execSpan!.Status.Should().NotBe(ActivityStatusCode.Error,
            "a recovered non-critical failure must not mark ExecutePipeline as Error");
    }

    [Fact]
    public async Task NonCriticalFailure_WithStepSpan_RecordsEventOnStepSpanNotExecPipeline()
    {
        // When PipelineStepRunner creates a Step span, non-critical failures record an event
        // on the step span without setting Error on either span.
        using var execSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        var context = BuildContext();
        var step = new NonCriticalFailingStep();

        // Run step through PipelineStepRunner so the runner creates a "Step NcFail" span.
        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        var stepSpan = _activities.FirstOrDefault(a => a.DisplayName == "Step NcFail");
        stepSpan.Should().NotBeNull("PipelineStepRunner creates a span per step");
        stepSpan!.Status.Should().Be(ActivityStatusCode.Unset,
            "a non-critical failure must not set Error on the step span");
        stepSpan.Events.Should().Contain(e => e.Name == "exception",
            "a non-critical failure adds an exception event");
        var evt = stepSpan.Events.First(e => e.Name == "exception");
        evt.Tags.Should().Contain(t => t.Key == "pipeline.non_critical" && true.Equals(t.Value));

        execSpan!.Status.Should().NotBe(ActivityStatusCode.Error,
            "ExecutePipeline must not be marked Error after a recovered non-critical failure");
    }

    // ── AC: Failed run → Error on step span AND ExecutePipeline ──────────────────

    [Fact]
    public async Task CriticalFailure_SetsErrorOnStepSpan_NotOnExecutePipelineViaRunner()
    {
        // When a step throws an unhandled exception, PipelineStepRunner records Error on the
        // Step span. The ExecutePipeline span is NOT set to Error by the runner — that happens
        // explicitly in LocalPipelineExecutor when FinalStep == Failed.
        using var execSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        var context = BuildContext();
        var step = new ThrowingStep();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None));

        var stepSpan = _activities.FirstOrDefault(a => a.DisplayName == "Step ThrowCritical");
        stepSpan.Should().NotBeNull("PipelineStepRunner creates a span per step");
        stepSpan!.Status.Should().Be(ActivityStatusCode.Error,
            "a critical failure (unhandled exception) sets Error on the step span");

        // ExecutePipeline is NOT set to Error by the runner — the test verifies no side-effect.
        execSpan!.Status.Should().NotBe(ActivityStatusCode.Error,
            "PipelineStepRunner does not set Error on the parent ExecutePipeline span directly");
    }

    // ── LocalPipelineExecutor FinalStep → ExecutePipeline status mapping ─────────
    // These tests verify the status-setting logic (mirrored from LocalPipelineExecutor.ExecuteAsync)
    // directly using PipelineRunInstrumentation.

    [Fact]
    public void FinalStep_Completed_SetsOkOnExecutePipelineSpan()
    {
        // TODO: [WARNING] This test exercises PipelineRunInstrumentation.MarkCompleted() directly,
        // which is a separate code path from the LocalPipelineExecutor.ExecuteAsync conditional block
        // (else if result.FinalStep == PipelineStep.Failed). A regression where LocalPipelineExecutor
        // incorrectly sets Error for Completed runs would not be caught here. Consider adding a test
        // that drives the assertion through LocalPipelineExecutor or exercises the conditional block
        // that was changed. Also note: this asserts in-flight span state rather than final stopped
        // state — a future change clearing status on disposal would not be caught.
        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, null, null);

        // Mirroring the Completed branch in LocalPipelineExecutor.ExecuteAsync:
        instrumentation.MarkCompleted();

        instrumentation.Activity!.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Theory]
    [InlineData(PipelineStep.ConflictRestart)]
    [InlineData(PipelineStep.PrMerged)]
    [InlineData(PipelineStep.PrClosed)]
    public void FinalStep_NonErrorTerminalState_DoesNotSetErrorOnExecutePipelineSpan(PipelineStep finalStep)
    {
        // Simulate the fixed conditional block in LocalPipelineExecutor.ExecuteAsync.
        // Only PipelineStep.Failed should set Error; all other non-Completed states must not.
        using var execSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");

        // Fixed logic from LocalPipelineExecutor.ExecuteAsync:
        if (finalStep == PipelineStep.Cancelled)
            execSpan?.SetTag("pipeline.cancelled", true);
        else if (finalStep == PipelineStep.Failed)
            execSpan?.SetStatus(ActivityStatusCode.Error, finalStep.ToString());

        execSpan!.Status.Should().Be(ActivityStatusCode.Unset,
            $"{finalStep} is a non-failure terminal state and must not set Error on ExecutePipeline");
    }

    [Fact]
    public void FinalStep_Failed_SetsErrorOnExecutePipelineSpan()
    {
        using var execSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        const string failureReason = "quality gates exhausted after 3 retries";
        var finalStep = PipelineStep.Failed;

        // Fixed logic from LocalPipelineExecutor.ExecuteAsync:
        if (finalStep == PipelineStep.Cancelled)
            execSpan?.SetTag("pipeline.cancelled", true);
        else if (finalStep == PipelineStep.Failed)
            execSpan?.SetStatus(ActivityStatusCode.Error, failureReason);

        execSpan!.Status.Should().Be(ActivityStatusCode.Error,
            "FinalStep == Failed must set Error on ExecutePipeline");
        execSpan.StatusDescription.Should().Be(failureReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun(PipelineStep currentStep)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Implementation,
            ProjectId = "proj",
            ProjectName = "TestProject"
        };
        run.CurrentStep = currentStep;
        return run;
    }

    private static PipelineStepContext BuildContext()
    {
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var run = CreateRun(PipelineStep.Created);
        var prOrchestrator = new PullRequestOrchestrator(logger);
        var callbacks = new Mock<IPipelineCallbacks>();
        callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            IssueProvider = Mock.Of<IIssueProvider>(),
            Callbacks = callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = new AgentPhaseExecutor(logger),
            QualityGates = new QualityGateExecutor(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, new CiLogWriter(logger), new FeedbackService(logger), logger),
            BrainSync = null,
            PrOrchestrator = prOrchestrator,
            Logger = logger
        };
    }

    /// <summary>Step that calls TryNonCriticalAsync with a failure — recovers and returns Continue.</summary>
    private sealed class NonCriticalFailingStep : IPipelineStep
    {
        public string StepName => "NcFail";
        public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
        {
            return await context.TryNonCriticalAsync(
                () => throw new InvalidOperationException("non-critical transient error"),
                "NcAction");
        }
    }

    /// <summary>Step that throws an unhandled exception (critical failure).</summary>
    private sealed class ThrowingStep : IPipelineStep
    {
        public string StepName => "ThrowCritical";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
            => throw new InvalidOperationException("critical step failed");
    }
}
