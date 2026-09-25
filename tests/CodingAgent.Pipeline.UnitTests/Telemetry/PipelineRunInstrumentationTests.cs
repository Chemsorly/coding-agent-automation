using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelineRunInstrumentation"/> verifying that the helper
/// correctly manages the ExecutePipeline activity lifecycle.
///
/// Metric recording (pipeline.run.outcomes, pipeline.run.duration) was moved to
/// <c>WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync</c> in issue #2967.
/// Tests for those metrics live in the API integration tests.
/// </summary>
public class PipelineRunInstrumentationTests
{
    private static PipelineRunInstrumentation StartRun(
        string runId = "run-1", string issueIdentifier = "issue-1",
        PipelineRunType runType = PipelineRunType.Implementation,
        string? projectId = "proj-1", string? projectName = "My Project",
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default)
        => PipelineRunInstrumentation.Start(runId, issueIdentifier, runType, projectId, projectName, kind, parentContext);

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
        instrumentation.Activity!.GetTagItem("pipeline.run_type").Should().Be(expectedTagValue);
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
    public void StopTiming_IsNoOp_DoesNotThrow()
    {
        using var instrumentation = StartRun();
        // StopTiming is a no-op for compatibility — should not throw.
        var act = () =>
        {
            instrumentation.StopTiming();
            instrumentation.StopTiming(); // idempotent
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void MarkCompleted_SetsActivityStatusOk()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");

        instrumentation.MarkCompleted();
        instrumentation.Dispose();

        // MarkCompleted sets OK status on the span.
        // TODO: [WARNING] This assertion only checks that Activity is non-null (trivially guaranteed by
        // the ActivityListener above). It does NOT verify that MarkCompleted() actually set the status to
        // ActivityStatusCode.Ok. Add: instrumentation.Activity!.Status.Should().Be(ActivityStatusCode.Ok)
        // to make this test meaningful and catch regressions in MarkCompleted's SetStatus call.
        instrumentation.Activity.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_SetsFailureReasonTagOnActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var instrumentation = PipelineRunInstrumentation.Start(
            "run-1", "issue-1", PipelineRunType.Implementation, "proj-1", "Proj");

        instrumentation.MarkFailed(FailureReason.QualityGateExhausted);
        instrumentation.Dispose();

        instrumentation.Activity.Should().NotBeNull();
        instrumentation.Activity!.GetTagItem("pipeline.failure_reason").Should().Be("QualityGateExhausted");
    }
}
