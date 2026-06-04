using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying quality gate metric instruments emit correct tags.
/// </summary>
public class PipelineTelemetryQualityGateTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string InstrumentName, List<KeyValuePair<string, object?>> Tags)> _measurements = [];

    public PipelineTelemetryQualityGateTests()
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
            _measurements.Add((instrument.Name, tagList));
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            var tagList = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags)
                tagList.Add(tag);
            _measurements.Add((instrument.Name, tagList));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void QualityGateRetries_Add_IncludesRunTypeTag()
    {
        PipelineTelemetry.QualityGateRetries.Add(1, PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation));

        var entry = _measurements.Should().ContainSingle(m => m.InstrumentName == "quality_gate.retries").Subject;
        entry.Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
    }

    [Theory]
    [InlineData(true, "pass")]
    [InlineData(false, "fail")]
    public void QualityGateEvaluations_Add_IncludesGateNameAndResult(bool passed, string expectedResult)
    {
        _measurements.Clear();

        PipelineTelemetry.QualityGateEvaluations.Add(1,
            new("gate_name", PipelineTelemetry.QualityGateNames.Compilation),
            new("result", passed ? "pass" : "fail"));

        var entry = _measurements.Should().ContainSingle(m => m.InstrumentName == "quality_gate.evaluations").Subject;
        entry.Tags.Should().Contain(new KeyValuePair<string, object?>("gate_name", "compilation"));
        entry.Tags.Should().Contain(new KeyValuePair<string, object?>("result", expectedResult));
    }

    [Fact]
    public void QualityGateDuration_Record_AcceptsValue()
    {
        PipelineTelemetry.QualityGateDuration.Record(42.5,
            PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        _measurements.Should().ContainSingle(m => m.InstrumentName == "quality_gate.duration");
    }

    [Fact]
    public void ExternalCiDuration_Record_AcceptsValue()
    {
        PipelineTelemetry.ExternalCiDuration.Record(120.0,
            PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        _measurements.Should().ContainSingle(m => m.InstrumentName == "quality_gate.external_ci.duration");
    }

    [Fact]
    public void QualityGateNames_AreStableConstants()
    {
        PipelineTelemetry.QualityGateNames.Compilation.Should().Be("compilation");
        PipelineTelemetry.QualityGateNames.Tests.Should().Be("tests");
        PipelineTelemetry.QualityGateNames.Coverage.Should().Be("coverage");
        PipelineTelemetry.QualityGateNames.Security.Should().Be("security");
        PipelineTelemetry.QualityGateNames.ExternalCi.Should().Be("external_ci");
    }
}
