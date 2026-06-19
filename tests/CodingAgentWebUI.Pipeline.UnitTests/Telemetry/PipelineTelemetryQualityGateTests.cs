using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying quality gate metric instruments emit correct tags.
/// </summary>
[Collection("Metrics")]
public class PipelineTelemetryQualityGateTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string InstrumentName, KeyValuePair<string, object?>[] Tags)> _measurements = [];

    public PipelineTelemetryQualityGateTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _measurements.Add((instrument.Name, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            _measurements.Add((instrument.Name, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void QualityGateRetries_Add_IncludesRunTypeTag()
    {
        PipelineTelemetry.QualityGateRetries.Add(1, PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation));

        var entries = _measurements.Where(m => m.InstrumentName == "quality_gate.retries").ToList();
        entries.Should().NotBeEmpty();
        entries.Should().Contain(e =>
            e.Tags.Contains(new KeyValuePair<string, object?>("run_type", "implementation")));
    }

    [Theory]
    [InlineData(true, "pass")]
    [InlineData(false, "fail")]
    public void QualityGateEvaluations_Add_IncludesGateNameAndResult(bool passed, string expectedResult)
    {
        PipelineTelemetry.QualityGateEvaluations.Add(1,
            new("gate_name", PipelineTelemetry.QualityGateNames.Compilation),
            new("result", passed ? "pass" : "fail"));

        var entries = _measurements.Where(m => m.InstrumentName == "quality_gate.evaluations").ToList();
        entries.Should().NotBeEmpty();
        entries.Should().Contain(e =>
            e.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
            e.Tags.Contains(new KeyValuePair<string, object?>("result", expectedResult)));
    }

    [Fact]
    public void QualityGateDuration_Record_AcceptsValue()
    {
        PipelineTelemetry.QualityGateDuration.Record(42.5,
            PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        _measurements.Should().Contain(m => m.InstrumentName == "quality_gate.duration");
    }

    [Fact]
    public void ExternalCiDuration_Record_AcceptsValue()
    {
        PipelineTelemetry.ExternalCiDuration.Record(120.0,
            PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        _measurements.Should().Contain(m => m.InstrumentName == "quality_gate.external_ci.duration");
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
