using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

[Collection("Metrics")]
public class PipelineRunInstrumentationTests : IDisposable
{
    private readonly MeterListener _meterListener = new();
    private readonly ActivityListener _activityListener;
    private readonly ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _histograms = [];
    private readonly ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];
    private readonly ConcurrentBag<Activity> _activities = [];

    public PipelineRunInstrumentationTests()
    {
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            _histograms.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _counters.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _meterListener.Start();

        // Warm up: force the listener to observe all static instruments by emitting
        // a measurement on each. This eliminates the MeterListener race condition where
        // InstrumentPublished may not fire for instruments created before Start().
        PipelineTelemetry.JobsDispatched.Add(0);
        PipelineTelemetry.JobsCompleted.Add(0);
        PipelineTelemetry.JobsFailed.Add(0);
        PipelineTelemetry.JobDuration.Record(0);
        PipelineTelemetry.DecompositionDuration.Record(0);

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_activityListener);

        // Clear warm-up measurements
        _counters.Clear();
        _histograms.Clear();
    }

    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
    }

    [Fact]
    public void Start_IncrementsJobsDispatched()
    {
        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");

        _counters.Should().Contain(c => c.Name == "pipeline.jobs.dispatched" && c.Value == 1);
    }

    [Fact]
    // TODO: Assertion is weak — duration >= 0 would pass even with a broken stopwatch.
    // Consider verifying tags are correctly attached or introducing a small delay for a non-zero assertion.
    public void Dispose_RecordsJobDuration()
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");

        instrumentation.Dispose();

        _histograms.Should().Contain(h => h.Name == "pipeline.jobs.duration");
        var hist = _histograms.First(h => h.Name == "pipeline.jobs.duration");
        hist.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Dispose_WhenCompleted_IncrementsJobsCompleted()
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");
        instrumentation.MarkCompleted();

        instrumentation.Dispose();

        _counters.Should().Contain(c => c.Name == "pipeline.jobs.completed" && c.Value == 1);
        _counters.Should().NotContain(c => c.Name == "pipeline.jobs.failed" && c.Value == 1);
    }

    [Fact]
    public void Dispose_WhenNotCompleted_IncrementsJobsFailed()
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");

        instrumentation.Dispose();

        _counters.Should().Contain(c => c.Name == "pipeline.jobs.failed" && c.Value == 1);
        _counters.Should().NotContain(c => c.Name == "pipeline.jobs.completed" && c.Value == 1);
    }

    [Fact]
    public void Dispose_WhenCancelled_SetsCancelledTagAndIncrementsFailed()
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");
        instrumentation.MarkCancelled();
        instrumentation.SetFinalStep("Cancelled");

        instrumentation.Dispose();

        _counters.Should().Contain(c => c.Name == "pipeline.jobs.failed" && c.Value == 1);

        var activity = _activities.FirstOrDefault(a => a.OperationName == "ExecutePipeline");
        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.cancelled").Should().Be(true);
        activity.GetTagItem("pipeline.final_step").Should().Be("Cancelled");
    }

    [Fact]
    public void SetFinalStep_SetsActivityTag()
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");
        instrumentation.SetFinalStep("GeneratingCode");
        instrumentation.Dispose();

        var activity = _activities.FirstOrDefault(a => a.OperationName == "ExecutePipeline");
        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.final_step").Should().Be("GeneratingCode");
    }

    [Fact]
    public void Start_SetsRunIdAndIssueTagsOnActivity()
    {
        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-42", "org/repo#99", PipelineRunType.Review, "proj-2", "MyProj");

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.GetTagItem("pipeline.run_id").Should().Be("run-42");
        instrumentation.Activity.GetTagItem("pipeline.issue").Should().Be("org/repo#99");
        instrumentation.Activity.GetTagItem("pipeline.project_id").Should().Be("proj-2");
        instrumentation.Activity.GetTagItem("pipeline.project_name").Should().Be("MyProj");
    }

    [Fact]
    public void Start_WithAgentId_SetsAgentIdTag()
    {
        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj",
            agentId: "agent-007");

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.GetTagItem("pipeline.agent_id").Should().Be("agent-007");
    }

    [Fact]
    public void Start_WithoutAgentId_DoesNotSetAgentIdTag()
    {
        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.GetTagItem("pipeline.agent_id").Should().BeNull();
    }

    [Fact]
    public void Start_WithParentContext_CreatesConsumerActivity()
    {
        // Create a parent activity to extract context from
        using var parent = PipelineTelemetry.ActivitySource.StartActivity("Parent", ActivityKind.Producer);
        parent.Should().NotBeNull();
        var parentContext = parent!.Context;

        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj",
            kind: ActivityKind.Consumer,
            parentContext: parentContext);

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.Kind.Should().Be(ActivityKind.Consumer);
        instrumentation.Activity.ParentId.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis, "analysis")]
    [InlineData(PipelineRunType.Decomposition, "creation")]
    public void Dispose_DecompositionRun_RecordsDecompositionDuration(PipelineRunType runType, string expectedPhase)
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", runType, "proj-1", "TestProj");

        instrumentation.Dispose();

        _histograms.Should().Contain(h => h.Name == "pipeline.decomposition.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("phase", expectedPhase)));
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation)]
    [InlineData(PipelineRunType.Review)]
    public void Dispose_NonDecompositionRun_DoesNotRecordDecompositionDuration(PipelineRunType runType)
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", runType, "proj-1", "TestProj");

        instrumentation.Dispose();

        _histograms.Should().NotContain(h => h.Name == "pipeline.decomposition.duration"
            && h.Tags.Any(t => t.Key == "pipeline.project_id" && (string?)t.Value == "proj-1"));
    }

    [Fact]
    public void Dispose_RecordsDurationTags_WithRunTypeAndProject()
    {
        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "org/repo#1", PipelineRunType.Implementation, "proj-1", "TestProj");

        instrumentation.Dispose();

        var hist = _histograms.First(h => h.Name == "pipeline.jobs.duration");
        hist.Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
        hist.Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "proj-1"));
        hist.Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "TestProj"));
    }

    // TODO: Add test for MarkCompleted + MarkCancelled both called before Dispose — MarkCompleted
    // wins for counter selection (implicit priority). Test this to prevent regressions if Dispose logic is reordered.

    // TODO: Add test that Dispose with no MarkCompleted/MarkCancelled does NOT set ActivityStatusCode.Error
    // on the activity. Callers are responsible for calling Activity?.SetStatus() — the helper only handles
    // counters and tags. This documents the caller contract and would catch accidental error-status setting.
}
