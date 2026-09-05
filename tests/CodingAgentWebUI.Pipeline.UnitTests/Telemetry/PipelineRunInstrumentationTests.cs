using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelineRunInstrumentation"/> verifying that the helper
/// correctly records metrics and manages activity lifecycle.
/// </summary>
public class PipelineRunInstrumentationTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();

    public PipelineRunInstrumentationTests() { }

    public void Dispose() => _meterFactory.Dispose();

    private PipelineRunInstrumentation StartRun(
        string runId = "run-1", string issueIdentifier = "issue-1",
        PipelineRunType runType = PipelineRunType.Implementation,
        string? projectId = "proj-1", string? projectName = "My Project",
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default)
        => PipelineRunInstrumentation.Start(runId, issueIdentifier, runType, projectId, projectName, kind, parentContext, _meterFactory);

    private MetricCollector<long> LongCollector(string instrumentName) =>
        new(_meterFactory, PipelineTelemetry.SourceName, instrumentName);

    private MetricCollector<double> DoubleCollector(string instrumentName) =>
        new(_meterFactory, PipelineTelemetry.SourceName, instrumentName);

    // ── Test methods use per-test collectors ─────────────────────────────────

    [Fact]
    public void Start_RecordsJobsDispatchedCounter()
    {
        using var dispatchedCollector = LongCollector("pipeline.jobs.dispatched");

        using var instrumentation = StartRun("run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "My Project");

        var snapshot = dispatchedCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle(m => m.Value == 1);
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "proj-1"));
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "My Project"));
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
        using var completedCollector = LongCollector("pipeline.jobs.completed");
        using var failedCollector = LongCollector("pipeline.jobs.failed");

        var instrumentation = StartRun();
        instrumentation.MarkCompleted();
        instrumentation.Dispose();

        completedCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
        failedCollector.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void Dispose_WithoutMarkCompleted_RecordsJobsFailed()
    {
        using var failedCollector = LongCollector("pipeline.jobs.failed");
        using var completedCollector = LongCollector("pipeline.jobs.completed");

        var instrumentation = StartRun();
        instrumentation.Dispose();

        var failedSnapshot = failedCollector.GetMeasurementSnapshot();
        failedSnapshot.Should().ContainSingle(m => m.Value == 1);
        failedSnapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("failure_reason", "unknown"));
        completedCollector.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void Dispose_RecordsJobDuration()
    {
        using var durationCollector = DoubleCollector("pipeline.jobs.duration");

        var instrumentation = StartRun(runType: PipelineRunType.Implementation);
        using var mres1 = new ManualResetEventSlim(false);
        mres1.Wait(10); // Ensure non-zero duration
        instrumentation.Dispose();

        var snapshot = durationCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().BeGreaterThan(0);
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
    }

    [Fact]
    public void Dispose_DoubleDispose_RecordsOnlyOnce()
    {
        using var failedCollector = LongCollector("pipeline.jobs.failed");
        using var durationCollector = DoubleCollector("pipeline.jobs.duration");

        var instrumentation = StartRun();
        instrumentation.Dispose();
        instrumentation.Dispose();

        failedCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
        durationCollector.GetMeasurementSnapshot().Should().ContainSingle();
    }

    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis, "analysis")]
    [InlineData(PipelineRunType.Decomposition, "creation")]
    public void Dispose_DecompositionRunType_RecordsDecompositionDuration(PipelineRunType runType, string expectedPhase)
    {
        using var decompositionCollector = DoubleCollector("pipeline.decomposition.duration");

        var instrumentation = StartRun(runType: runType, projectId: "proj-1", projectName: "Proj");
        instrumentation.Dispose();

        var snapshot = decompositionCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("phase", expectedPhase));
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "proj-1"));
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "Proj"));
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation)]
    [InlineData(PipelineRunType.Review)]
    public void Dispose_NonDecompositionRunType_DoesNotRecordDecompositionDuration(PipelineRunType runType)
    {
        using var decompositionCollector = DoubleCollector("pipeline.decomposition.duration");

        var instrumentation = StartRun(runType: runType);
        instrumentation.Dispose();

        decompositionCollector.GetMeasurementSnapshot().Should().BeEmpty();
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
        using var durationCollector = DoubleCollector("pipeline.jobs.duration");

        // Measure the true wall-clock from run start through a post-freeze wait. StopTiming freezes the
        // recorded duration at the pre-freeze point, so the frozen value excludes the post-freeze segment
        // and must therefore be strictly less than the measured total. This is robust to scheduling
        // jitter; the previous version assumed the pre-freeze segment was exactly 10ms (+0.010) and
        // flaked when that wait ran long under CI load, making the frozen value exceed the estimate.
        var startTimestamp = Stopwatch.GetTimestamp();
        var instrumentation = StartRun();
        using var mres2 = new ManualResetEventSlim(false);
        mres2.Wait(10); // Ensure a non-zero duration before freeze
        instrumentation.StopTiming();

        using var mres3 = new ManualResetEventSlim(false);
        mres3.Wait(50); // Time after the freeze — must NOT be counted in the frozen duration
        instrumentation.Dispose();

        var totalElapsedSeconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        var snapshot = durationCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().BeGreaterThan(0, "frozen duration must be non-zero");
        snapshot[0].Value.Should().BeLessThan(totalElapsedSeconds,
            "frozen duration must exclude the ~50ms after StopTiming — the timer must be frozen");
    }

    [Fact]
    public void StopTiming_IsIdempotent()
    {
        using var durationCollector = DoubleCollector("pipeline.jobs.duration");

        // Capture the upper-bound timestamp before starting the run so the
        // budget always covers the frozen value regardless of setup time.
        var startTimestamp = Stopwatch.GetTimestamp();

        var instrumentation = StartRun();
        using var mres4 = new ManualResetEventSlim(false);
        mres4.Wait(10); // Ensure non-zero duration before freeze

        instrumentation.StopTiming();

        using var mres5 = new ManualResetEventSlim(false);
        mres5.Wait(50); // Let at least 50ms pass after freeze

        instrumentation.StopTiming(); // must be no-ops
        instrumentation.StopTiming();
        instrumentation.Dispose();

        // Budget = total wall time from before the run started + 250 ms slack.
        // This is deliberately generous: we only care that subsequent StopTiming
        // calls do NOT push the recorded value beyond the first freeze, not that
        // the frozen value is small in absolute terms.
        var budgetSeconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds + 0.250;

        var snapshot = durationCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().BeGreaterThan(0, "frozen duration must be non-zero")
            .And.BeLessThan(budgetSeconds,
                "StopTiming must freeze elapsed time at first call; subsequent calls must not extend it");
    }

    [Fact]
    public void MarkCompleted_ThenStopTiming_StillRecordsCorrectStatus()
    {
        using var completedCollector = LongCollector("pipeline.jobs.completed");
        using var failedCollector = LongCollector("pipeline.jobs.failed");
        using var durationCollector = DoubleCollector("pipeline.jobs.duration");

        var instrumentation = StartRun();
        instrumentation.MarkCompleted();
        instrumentation.StopTiming();
        instrumentation.Dispose();

        completedCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
        failedCollector.GetMeasurementSnapshot().Should().BeEmpty();
        durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value > 0);
    }

    // ── failure_reason tag tests ─────────────────────────────────────────────

    [Fact]
    public void Dispose_WithoutMarkFailed_RecordsFailureReasonUnknown()
    {
        using var failedCollector = LongCollector("pipeline.jobs.failed");

        var instrumentation = StartRun();
        instrumentation.Dispose();

        var snapshot = failedCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle(m => m.Value == 1);
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("failure_reason", "unknown"));
    }

    [Fact]
    public void Dispose_WithMarkFailed_KnownReason_RecordsSnakeCaseTag()
    {
        using var failedCollector = LongCollector("pipeline.jobs.failed");

        var instrumentation = StartRun();
        instrumentation.MarkFailed(FailureReason.QualityGateExhausted);
        instrumentation.Dispose();

        var snapshot = failedCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle(m => m.Value == 1);
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("failure_reason", "quality_gate_exhausted"));
    }

    [Fact]
    public void Dispose_WithMarkFailed_NullReason_RecordsUnknown()
    {
        using var failedCollector = LongCollector("pipeline.jobs.failed");

        var instrumentation = StartRun();
        instrumentation.MarkFailed(null);
        instrumentation.Dispose();

        var snapshot = failedCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle(m => m.Value == 1);
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("failure_reason", "unknown"));
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
        using var failedCollector = LongCollector("pipeline.jobs.failed");

        var instrumentation = StartRun();
        instrumentation.MarkFailed(reason);
        instrumentation.Dispose();

        var snapshot = failedCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle(m => m.Value == 1);
        snapshot[0].Tags.Should().Contain(new KeyValuePair<string, object?>("failure_reason", expectedTag));
    }

    [Fact]
    public void Dispose_WithMarkCompleted_DoesNotIncludeFailureReasonTag()
    {
        using var completedCollector = LongCollector("pipeline.jobs.completed");
        using var failedCollector = LongCollector("pipeline.jobs.failed");

        var instrumentation = StartRun();
        instrumentation.MarkCompleted();
        instrumentation.Dispose();

        var completedSnapshot = completedCollector.GetMeasurementSnapshot();
        completedSnapshot.Should().ContainSingle(m => m.Value == 1);
        completedSnapshot[0].Tags.Should().NotContain(t => t.Key == "failure_reason");
        failedCollector.GetMeasurementSnapshot().Should().BeEmpty();
    }
}
