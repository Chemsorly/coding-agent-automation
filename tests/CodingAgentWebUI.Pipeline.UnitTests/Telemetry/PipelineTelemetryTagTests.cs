using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying that pipeline metrics include the run_type tag.
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
}
