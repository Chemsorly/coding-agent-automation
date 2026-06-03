using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying that pipeline metrics include the run_type and project tags.
/// </summary>
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
            if (instrument.Name == "pipeline.jobs.dispatched")
            {
                foreach (var tag in tags)
                    _capturedTags.Add(tag);
            }
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Theory]
    [InlineData(PipelineRunType.Implementation, "implementation")]
    [InlineData(PipelineRunType.Review, "review")]
    [InlineData(PipelineRunType.DecompositionAnalysis, "decompositionanalysis")]
    [InlineData(PipelineRunType.Decomposition, "decomposition")]
    public void JobsDispatched_Add_IncludesRunTypeTag(PipelineRunType runType, string expected)
    {
        PipelineTelemetry.JobsDispatched.Add(1, PipelineTelemetry.RunTypeTag(runType));

        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("run_type", expected));
    }

    [Fact]
    public void BuildTags_IncludesProjectIdAndProjectName()
    {
        var tags = PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-123", "MyProject");

        PipelineTelemetry.JobsDispatched.Add(1, tags);

        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "proj-123"));
        _capturedTags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "MyProject"));
    }

    [Fact]
    public void BuildTags_NullProjectId_EmitsUnknown()
    {
        var tags = PipelineTelemetry.BuildTags(PipelineRunType.Implementation, null, null);

        PipelineTelemetry.JobsDispatched.Add(1, tags);

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
