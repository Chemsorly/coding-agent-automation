using System.Collections.Concurrent;
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
/// Tests that <see cref="PipelineStepRunner"/> creates a named span per step and that
/// span attributes are set correctly.
/// </summary>
public class PipelineStepRunnerSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    // ConcurrentBag prevents "Collection was modified; enumeration operation may not execute"
    // when the ActivityStopped callback fires from a thread-pool thread concurrently with
    // the assertion-phase enumeration.
    private readonly ConcurrentBag<Activity> _activities = [];

    public PipelineStepRunnerSpanTests()
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

    [Fact]
    public async Task ExecuteAsync_SingleStep_CreatesNamedStepSpan()
    {
        var context = BuildContext("run-span-1");
        var step = new NamedStep("MyStep");

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        var snapshot = _activities.ToList();
        var stepSpan = snapshot.FirstOrDefault(a => a.DisplayName == "Step MyStep");
        stepSpan.Should().NotBeNull("PipelineStepRunner must create a 'Step {StepName}' span");
    }

    [Fact]
    public async Task ExecuteAsync_SingleStep_SetsPipelineStepAndRunIdTags()
    {
        var context = BuildContext("run-tags-test");
        var step = new NamedStep("TaggedStep");

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        var snapshot = _activities.ToList();
        var stepSpan = snapshot.FirstOrDefault(a => a.DisplayName == "Step TaggedStep");
        stepSpan.Should().NotBeNull();
        stepSpan!.GetTagItem("pipeline.step").Should().Be("TaggedStep");
        stepSpan.GetTagItem("pipeline.run_id").Should().Be("run-tags-test");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleSteps_CreatesOneSpanPerStep()
    {
        var context = BuildContext("run-multi");
        var step1 = new NamedStep("Alpha");
        var step2 = new NamedStep("Beta");
        var step3 = new NamedStep("Gamma");

        await PipelineStepRunner.ExecuteAsync([step1, step2, step3], context, CancellationToken.None);

        // Snapshot once to avoid "Collection was modified" races with the listener callback.
        var snapshot = _activities.ToList();
        snapshot.Should().Contain(a => a.DisplayName == "Step Alpha");
        snapshot.Should().Contain(a => a.DisplayName == "Step Beta");
        snapshot.Should().Contain(a => a.DisplayName == "Step Gamma");
        // Filter by run_id to avoid counting Step spans from other tests running in parallel
        // that share the same global ActivityListener.
        snapshot.Count(a => a.DisplayName.StartsWith("Step ")
            && Equals(a.GetTagItem("pipeline.run_id"), "run-multi")).Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_StepReturnsStop_SpanIsEmittedWithNoError()
    {
        var context = BuildContext("run-stop");
        var step = new StoppingStep("Stopper");

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        var snapshot = _activities.ToList();
        var stepSpan = snapshot.FirstOrDefault(a => a.DisplayName == "Step Stopper");
        stepSpan.Should().NotBeNull("a stopping step still gets a span");
        stepSpan!.Status.Should().NotBe(ActivityStatusCode.Error,
            "StepResult.Stop is not an error — the span must remain Unset");
    }

    [Fact]
    public async Task ExecuteAsync_StepThrows_ErrorRecordedOnStepSpanNotParent()
    {
        // Verify the core fix: errors go to the Step span, not the parent ExecutePipeline span.
        using var parentSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        var context = BuildContext("run-err");
        var step = new ThrowingStep("ErrorStep");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None));

        var snapshot = _activities.ToList();
        var stepSpan = snapshot.FirstOrDefault(a => a.DisplayName == "Step ErrorStep");
        stepSpan.Should().NotBeNull();
        stepSpan!.Status.Should().Be(ActivityStatusCode.Error,
            "a throwing step must set Error status on its own Step span");

        parentSpan!.Status.Should().NotBe(ActivityStatusCode.Error,
            "the parent ExecutePipeline span must not be set Error by PipelineStepRunner");
    }

    [Fact]
    public async Task ExecuteAsync_StepCallsTryCriticalAsync_ErrorOnStepSpan()
    {
        // When a step (without its own inner span) calls TryCriticalAsync, the error lands on
        // the runner-created Step span (Activity.Current) rather than ExecutePipeline.
        using var parentSpan = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        var context = BuildContext("run-critical");
        var step = new CriticalFailingStep("CriticalStep");

        // CriticalFailingStep calls TryCriticalAsync which fails and returns StepResult.Stop.
        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        var snapshot = _activities.ToList();
        var stepSpan = snapshot.FirstOrDefault(a => a.DisplayName == "Step CriticalStep");
        stepSpan.Should().NotBeNull();
        stepSpan!.Status.Should().Be(ActivityStatusCode.Error,
            "TryCriticalAsync inside a step (with no inner span) must record Error on the runner's Step span");

        parentSpan!.Status.Should().NotBe(ActivityStatusCode.Error,
            "ExecutePipeline must not be polluted by TryCriticalAsync via Activity.Current");
    }

    [Fact]
    public async Task ExecuteAsync_StopOnFirstStop_OnlyFirstStepSpanEmitted()
    {
        var context = BuildContext("run-stop-at-first");
        var step1 = new StoppingStep("StopHere");
        var step2 = new NamedStep("ShouldNotRun");

        await PipelineStepRunner.ExecuteAsync([step1, step2], context, CancellationToken.None);

        var snapshot = _activities.ToList();
        snapshot.Should().Contain(a => a.DisplayName == "Step StopHere");
        snapshot.Should().NotContain(a => a.DisplayName == "Step ShouldNotRun");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static PipelineStepContext BuildContext(string runId)
    {
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Implementation,
            ProjectId = "proj",
            ProjectName = "TestProject"
        };
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

    private sealed class NamedStep : IPipelineStep
    {
        public NamedStep(string name) => StepName = name;
        public string StepName { get; }
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
            => Task.FromResult(StepResult.Continue);
    }

    private sealed class StoppingStep : IPipelineStep
    {
        public StoppingStep(string name) => StepName = name;
        public string StepName { get; }
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
            => Task.FromResult(StepResult.Stop);
    }

    private sealed class ThrowingStep : IPipelineStep
    {
        public ThrowingStep(string name) => StepName = name;
        public string StepName { get; }
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
            => throw new InvalidOperationException("step threw");
    }

    /// <summary>Step that calls TryCriticalAsync with a failing action (no inner activity).</summary>
    private sealed class CriticalFailingStep : IPipelineStep
    {
        public CriticalFailingStep(string name) => StepName = name;
        public string StepName { get; }
        public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
        {
            return await context.TryCriticalAsync(
                () => throw new InvalidOperationException("critical action failed"),
                "CriticalAction");
        }
    }
}
