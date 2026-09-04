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
        instrumentation.Activity.GetTagItem("pipeline.run_type").Should().Be("Review");
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation, "Implementation")]
    [InlineData(PipelineRunType.Review, "Review")]
    [InlineData(PipelineRunType.DecompositionAnalysis, "DecompositionAnalysis")]
    [InlineData(PipelineRunType.Decomposition, "Decomposition")]
    [InlineData(PipelineRunType.Consolidation, "Consolidation")]
    public void Start_SetsRunTypeTagOnActivity(PipelineRunType runType, string expectedTagValue)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "owner/repo#1", runType, "proj-1", "Project 1");

        instrumentation.Activity.Should().NotBeNull();
        // Span tag must be PascalCase (e.g. "Implementation"), NOT lowercased.
        // Metric tag uses lowercase via PipelineTelemetry.RunTypeTag() — that is a separate concern.
        // See docs/internals/observability-internals.md: "span pipeline.run_type values are PascalCase".
        instrumentation.Activity!.GetTagItem("pipeline.run_type").Should().Be(expectedTagValue);
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
        failed[0].Tags.Should().Contain(t => t.Key == "failure_reason" && (string?)t.Value == "unknown");

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

    [Fact]
    public void StopTiming_FreezesElapsedDuration()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        using var mres2 = new ManualResetEventSlim(false);
        mres2.Wait(10); // Ensure non-zero duration before freeze
        instrumentation.StopTiming();

        // Record the freeze point, then let more time pass.
        // We assert that the recorded duration is less than the total elapsed time,
        // proving the timer was frozen by StopTiming(). This is a relative assertion
        // independent of absolute wall-clock speed — no fixed threshold needed.
        var freezeTimestamp = Stopwatch.GetTimestamp();
        using var mres3 = new ManualResetEventSlim(false);
        mres3.Wait(50); // Let at least 50ms pass after freeze
        var totalElapsedSeconds = Stopwatch.GetElapsedTime(freezeTimestamp).TotalSeconds + 0.010; // +10ms = pre-freeze segment

        instrumentation.Dispose();

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        duration[0].Value.Should().BeGreaterThan(0, "frozen duration must be non-zero");
        duration[0].Value.Should().BeLessThan(totalElapsedSeconds,
            "frozen duration must be less than total elapsed time — StopTiming must have frozen the timer");
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

        // Record freeze point, then let time pass and call StopTiming again (must be no-ops).
        var freezeTimestamp = Stopwatch.GetTimestamp();
        using var mres5 = new ManualResetEventSlim(false);
        mres5.Wait(50); // Let at least 50ms pass after freeze

        instrumentation.StopTiming(); // must be no-ops
        instrumentation.StopTiming();
        instrumentation.Dispose();

        var totalElapsedSeconds = Stopwatch.GetElapsedTime(freezeTimestamp).TotalSeconds + 0.010; // +10ms = pre-freeze segment

        var duration = _doubleMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        // Lower bound: catches regressions that zero out the duration.
        // Upper bound: relative — frozen value must be less than total elapsed, proving subsequent
        // StopTiming() calls did not extend the recorded duration.
        duration[0].Value.Should().BeGreaterThan(0, "frozen duration must be non-zero")
            .And.BeLessThan(totalElapsedSeconds,
                "StopTiming must freeze elapsed time at first call; subsequent calls must not extend it");
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

    // ── failure_reason tag tests ─────────────────────────────────────────────

    [Fact]
    public void Dispose_WithoutMarkFailed_RecordsFailureReasonUnknown()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.Dispose();

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().HaveCount(1);
        failed[0].Tags.Should().Contain(t => t.Key == "failure_reason" && (string?)t.Value == "unknown");
    }

    [Fact]
    public void Dispose_WithMarkFailed_KnownReason_RecordsSnakeCaseTag()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.MarkFailed(FailureReason.QualityGateExhausted);
        instrumentation.Dispose();

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().HaveCount(1);
        failed[0].Tags.Should().Contain(t => t.Key == "failure_reason" && (string?)t.Value == "quality_gate_exhausted");
    }

    [Fact]
    public void Dispose_WithMarkFailed_NullReason_RecordsUnknown()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.MarkFailed(null);
        instrumentation.Dispose();

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().HaveCount(1);
        failed[0].Tags.Should().Contain(t => t.Key == "failure_reason" && (string?)t.Value == "unknown");
    }

    [Theory]
    [InlineData(FailureReason.Timeout, "timeout")]
    [InlineData(FailureReason.InfrastructureFailure, "infrastructure_failure")]
    [InlineData(FailureReason.AgentError, "agent_error")]
    [InlineData(FailureReason.TokenRefreshFailure, "token_refresh_failure")]
    [InlineData(FailureReason.ExitCodeFailure, "exit_code_failure")]
    [InlineData(FailureReason.QualityGateExhausted, "quality_gate_exhausted")]
    public void Dispose_WithMarkFailed_AllReasons_ProduceSnakeCaseTag(FailureReason reason, string expectedTag)
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.MarkFailed(reason);
        instrumentation.Dispose();

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().HaveCount(1);
        failed[0].Tags.Should().Contain(t => t.Key == "failure_reason" && (string?)t.Value == expectedTag);
    }

    [Fact]
    public void Dispose_WithMarkCompleted_DoesNotIncludeFailureReasonTag()
    {
        ClearMeasurements();

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");
        instrumentation.MarkCompleted();
        instrumentation.Dispose();

        var completed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.completed" && m.Value == 1).ToList();
        completed.Should().HaveCount(1);
        completed[0].Tags.Should().NotContain(t => t.Key == "failure_reason");

        var failed = _longMeasurements.Where(m => m.InstrumentName == "pipeline.jobs.failed" && m.Value == 1).ToList();
        failed.Should().BeEmpty();
    }
}
