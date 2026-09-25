using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying that pipeline metric tag helpers produce the correct values.
/// Uses <see cref="PipelineTelemetry.RunOutcomes"/> as the emission vehicle (formerly
/// <c>JobsDispatched</c>, which was removed in issue #2967).
/// </summary>
// TODO: [WARNING] This class is NOT in the [Collection("Metrics")] xUnit collection. Tests
// that emit on the static PipelineTelemetry.Meter may interfere with other metric tests.
// Fix: add [Collection("Metrics")] to serialise execution with other metric test classes.
public class PipelineTelemetryTagTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<KeyValuePair<string, object?>> _capturedTags = [];

    public PipelineTelemetryTagTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "pipeline.run.outcomes")
            {
                foreach (var tag in tags)
                    _capturedTags.Add(tag);
            }
        });

        _listener.Start();
        // Warm-up: ensure RunOutcomes is observed before any test assertion.
        PipelineTelemetry.RunOutcomes.Add(0);
        _capturedTags.Clear();
    }

    public void Dispose() => _listener.Dispose();

    [Theory]
    [InlineData(PipelineRunType.Implementation, "implementation")]
    [InlineData(PipelineRunType.Review, "review")]
    [InlineData(PipelineRunType.DecompositionAnalysis, "decompositionanalysis")]
    [InlineData(PipelineRunType.Decomposition, "decomposition")]
    [InlineData(PipelineRunType.Consolidation, "consolidation")]
    public void RunTypeTag_ProducesCorrectLowercaseValue(PipelineRunType runType, string expected)
    {
        // Verify the tag helper produces the correct value.
        var tag = PipelineTelemetry.RunTypeTag(runType);
        tag.Key.Should().Be("run_type");
        tag.Value.Should().Be(expected);

        // Also verify emission via listener.
        _capturedTags.Clear();
        PipelineTelemetry.RunOutcomes.Add(1, PipelineTelemetry.RunTypeTag(runType));

        _capturedTags.Should().Contain(t => t.Key == "run_type" && (string?)t.Value == expected);
    }

    [Fact]
    public void BuildTags_IncludesProjectIdAndProjectName()
    {
        var tags = PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-123", "MyProject");

        _capturedTags.Clear();
        PipelineTelemetry.RunOutcomes.Add(1, tags);

        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "proj-123"));
        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "MyProject"));
    }

    [Fact]
    public void BuildTags_NullProjectId_EmitsUnknown()
    {
        var tags = PipelineTelemetry.BuildTags(PipelineRunType.Implementation, null, null);

        _capturedTags.Clear();
        PipelineTelemetry.RunOutcomes.Add(1, tags);

        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "unknown"));
        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "unknown"));
    }

    [Fact]
    public void SetProjectTags_SetsTagsOnActivity()
    {
        using var source = new ActivitySource("test.telemetry.projects");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.telemetry.projects",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("TestOp");
        PipelineTelemetry.SetProjectTags(activity, "proj-456", "TestProject");

        activity!.GetTagItem("pipeline.project_id").Should().Be("proj-456");
        activity.GetTagItem("pipeline.project_name").Should().Be("TestProject");
    }

    [Fact]
    public void SetProjectTags_NullValues_SetsUnknown()
    {
        using var source = new ActivitySource("test.telemetry.projects.null");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.telemetry.projects.null",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("TestOp");
        PipelineTelemetry.SetProjectTags(activity, null, null);

        activity!.GetTagItem("pipeline.project_id").Should().Be("unknown");
        activity.GetTagItem("pipeline.project_name").Should().Be("unknown");
    }
}
