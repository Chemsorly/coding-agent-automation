using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelineRunInstrumentation"/> verifying that the helper
/// correctly records metrics and manages activity lifecycle.
/// </summary>
[Collection("Metrics")]
public class PipelineRunInstrumentationTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string InstrumentName, double Value, List<KeyValuePair<string, object?>> Tags)> _doubleMeasurements = [];
    private readonly ConcurrentBag<(string InstrumentName, long Value, List<KeyValuePair<string, object?>> Tags)> _longMeasurements = [];

    public PipelineRunInstrumentationTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            var tagList = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags)
                tagList.Add(tag);
            _longMeasurements.Add((instrument.Name, measurement, tagList));
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            var tagList = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags)
                tagList.Add(tag);
            _doubleMeasurements.Add((instrument.Name, measurement, tagList));
        });

        _listener.Start();

        // Warm up static instruments
        PipelineTelemetry.JobsDispatched.Add(0);
        PipelineTelemetry.JobsCompleted.Add(0);
        PipelineTelemetry.JobsFailed.Add(0);
        PipelineTelemetry.JobDuration.Record(0);
        PipelineTelemetry.DecompositionDuration.Record(0);

        ClearMeasurements();
    }

    public void Dispose() => _listener.Dispose();

    private void ClearMeasurements()
    {
        _longMeasurements.Clear();
        _doubleMeasurements.Clear();
    }

    [Fact]
    public void Start_RecordsJobsDispatchedCounter()
    {
        ClearMeasurements();

        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "My Project");

        var dispatched = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.dispatched" && m.Value == 1).ToList();
        dispatched.Should().HaveCount(1);
        dispatched[0].Tags.Should().Contain(t => t.Key == "run_type" && (string?)t.Value == "implementation");
        dispatched[0].Tags.Should().Contain(t => t.Key == "pipeline.project_id" && (string?)t.Value == "proj-1");
        dispatched[0].Tags.Should().Contain(t => t.Key == "pipeline.project_name" && (string?)t.Value == "My Project");
    }

    [Fact]
    public void Start_CreatesActivityWithStandardTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-123", "owner/repo#42", PipelineRunType.Review, "proj-A", "Project A");

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.DisplayName.Should().Be("ExecutePipeline");
        instrumentation.Activity.GetTagItem("pipeline.run_id").Should().Be("run-123");
        instrumentation.Activity.GetTagItem("pipeline.issue").Should().Be("owner/repo#42");
        instrumentation.Activity.GetTagItem("pipeline.project_id").Should().Be("proj-A");
        instrumentation.Activity.GetTagItem("pipeline.project_name").Should().Be("Project A");
    }

    [Fact]
    public void Dispose_WithMarkCompleted_RecordsJobsCompleted()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.MarkCompleted();
        instrumentation.Dispose();

        var completed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.completed" && m.Value == 1).ToList();
        completed.Should().HaveCount(1);

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_WithoutMarkCompleted_RecordsJobsFailed()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.Dispose();

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().HaveCount(1);

        var completed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.completed" && m.Value == 1).ToList();
        completed.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_RecordsJobDuration()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        using var mres1 = new ManualResetEventSlim(false);
        mres1.Wait(10); // Ensure non-zero duration
        instrumentation.Dispose();

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        duration[0].Value.Should().BeGreaterThan(0);
        duration[0].Tags.Should().Contain(t => t.Key == "run_type" && (string?)t.Value == "implementation");
    }

    [Fact]
    public void Dispose_DoubleDispose_RecordsOnlyOnce()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.Dispose();
        instrumentation.Dispose();

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().HaveCount(1);

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis, "analysis")]
    [InlineData(PipelineRunType.Decomposition, "creation")]
    public void Dispose_DecompositionRunType_RecordsDecompositionDuration(PipelineRunType runType, string expectedPhase)
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", runType, "proj-1", "Proj");
        instrumentation.Dispose();

        var decomposition = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.decomposition.duration").ToList();
        decomposition.Should().HaveCount(1);
        decomposition[0].Tags.Should().Contain(t => t.Key == "phase" && (string?)t.Value == expectedPhase);
        decomposition[0].Tags.Should().Contain(t => t.Key == "pipeline.project_id" && (string?)t.Value == "proj-1");
        decomposition[0].Tags.Should().Contain(t => t.Key == "pipeline.project_name" && (string?)t.Value == "Proj");
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation)]
    [InlineData(PipelineRunType.Review)]
    public void Dispose_NonDecompositionRunType_DoesNotRecordDecompositionDuration(PipelineRunType runType)
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", runType, "proj-1", "Proj");
        instrumentation.Dispose();

        var decomposition = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.decomposition.duration").ToList();
        decomposition.Should().BeEmpty();
    }

    [Fact]
    public void Start_WithConsumerKindAndParentContext_CreatesCorrectActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var parentContext = new ActivityContext(parentTraceId, parentSpanId, ActivityTraceFlags.Recorded);

        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj",
            ActivityKind.Consumer, parentContext);

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.Kind.Should().Be(ActivityKind.Consumer);
        instrumentation.Activity.ParentId.Should().Contain(parentTraceId.ToString());
    }

    [Fact]
    public void Dispose_DisposesActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");

        var activity = instrumentation.Activity;
        activity.Should().NotBeNull();

        instrumentation.Dispose();

        // After disposal, the activity should be stopped (Duration > TimeSpan.Zero indicates it was stopped)
        activity!.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    // TODO: Thread.Sleep-based timing assertions are inherently non-deterministic and could flake under
    // heavy CI load. The 10ms sleep + BeLessThan(0.05) threshold provides 40ms margin but consider
    // increasing delays or asserting relative ordering. The unused frozenTime variable suggests an
    // incomplete assertion that was going to verify something more specific.
    [Fact]
    public void StopTiming_FreezesElapsedDuration()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        using var mres2 = new ManualResetEventSlim(false);
        mres2.Wait(10); // Ensure non-zero duration
        instrumentation.StopTiming();

        var frozenTime = Stopwatch.GetTimestamp();
        using var mres3 = new ManualResetEventSlim(false);
        mres3.Wait(500); // Simulate expensive cleanup after StopTiming

        instrumentation.Dispose();

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        // Duration should be much less than the total elapsed time (should not include the 500ms sleep).
        // Use a generous threshold (0.5s) to tolerate CI jitter on the initial segment,
        // while still proving the timer froze (unfrozen would be > 0.5s).
        duration[0].Value.Should().BeLessThan(0.5);
    }

    [Fact]
    public void StopTiming_IsIdempotent()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        using var mres4 = new ManualResetEventSlim(false);
        mres4.Wait(10); // Ensure non-zero duration before freeze

        instrumentation.StopTiming();
        using var mres5 = new ManualResetEventSlim(false);
        mres5.Wait(500); // 500ms after freeze point — frozen duration is ~10ms; unfrozen (no-op broken) would be ~510ms

        instrumentation.StopTiming(); // should be no-ops
        instrumentation.StopTiming();
        instrumentation.Dispose();

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        // Lower bound: catches regressions that zero out the duration.
        // Upper bound (0.3s): frozen path records ~10ms + CI jitter; unfrozen (broken) path would record ~510ms.
        // The 500ms post-freeze gap ensures a clear 17:1 ratio between the two cases, tolerating heavy CI load.
        duration[0].Value.Should().BeGreaterThan(0)
            .And.BeLessThan(0.3,
                "StopTiming should freeze elapsed time at first call; subsequent calls must not extend it");
    }

    [Fact]
    public void MarkCompleted_ThenStopTiming_StillRecordsCorrectStatus()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");

        // Mirrors actual usage: MarkCompleted() in try block, StopTiming() in finally block
        instrumentation.MarkCompleted();
        instrumentation.StopTiming();

        instrumentation.Dispose();

        var completed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.completed" && m.Value == 1).ToList();
        completed.Should().HaveCount(1);

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().BeEmpty();

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        duration[0].Value.Should().BeGreaterThan(0);
    }
}
