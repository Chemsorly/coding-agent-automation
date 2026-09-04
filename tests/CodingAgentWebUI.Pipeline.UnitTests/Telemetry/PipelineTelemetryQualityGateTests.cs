using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying quality gate metric instruments emit correct tags.
/// Uses <see cref="TestMeterFactory"/> and <see cref="MetricCollector{T}"/> for isolation —
/// no cross-test contamination via the shared static <see cref="PipelineTelemetry.Meter"/>.
/// </summary>
public class PipelineTelemetryQualityGateTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();
    private readonly System.Diagnostics.Metrics.Meter _meter;
    private readonly System.Diagnostics.Metrics.Counter<long> _qualityGateRetries;
    private readonly System.Diagnostics.Metrics.Counter<long> _qualityGateEvaluations;
    private readonly System.Diagnostics.Metrics.Histogram<double> _qualityGateDuration;
    private readonly System.Diagnostics.Metrics.Histogram<double> _externalCiDuration;
    private readonly System.Diagnostics.Metrics.Histogram<double> _postPrCiDuration;

    public PipelineTelemetryQualityGateTests()
    {
        _meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        _qualityGateRetries = _meter.CreateCounter<long>("quality_gate.retries", "{retry}", "Quality gate retry attempts");
        _qualityGateEvaluations = _meter.CreateCounter<long>("quality_gate.evaluations", "{evaluation}", "Individual gate evaluation events");
        _qualityGateDuration = _meter.CreateHistogram<double>("quality_gate.duration", "s", "Total time in quality gate phase");
        _externalCiDuration = _meter.CreateHistogram<double>("quality_gate.external_ci.duration", "s", "Time waiting for external CI");
        _postPrCiDuration = _meter.CreateHistogram<double>("quality_gate.post_pr_ci.duration", "s", "Time waiting for post-PR CI to complete");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void QualityGateRetries_Add_IncludesRunTypeTag()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "quality_gate.retries");

        _qualityGateRetries.Add(1, PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("run_type", "implementation")));
    }

    [Theory]
    [InlineData(true, "pass")]
    [InlineData(false, "fail")]
    public void QualityGateEvaluations_Add_IncludesGateNameAndResult(bool passed, string expectedResult)
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "quality_gate.evaluations");

        _qualityGateEvaluations.Add(1,
            new("gate_name", PipelineTelemetry.QualityGateNames.Compilation),
            new("result", passed ? "pass" : "fail"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
            m.Tags.Contains(new KeyValuePair<string, object?>("result", expectedResult)));
    }

    [Fact]
    public void QualityGateDuration_Record_AcceptsValue()
    {
        using var collector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "quality_gate.duration");

        _qualityGateDuration.Record(42.5, PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => Math.Abs(m.Value - 42.5) < 0.001);
    }

    [Fact]
    public void ExternalCiDuration_Record_AcceptsValue()
    {
        using var collector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "quality_gate.external_ci.duration");

        _externalCiDuration.Record(120.0, PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => Math.Abs(m.Value - 120.0) < 0.001);
    }

    [Fact]
    public void PostPrCiDuration_Record_AcceptsValue()
    {
        using var collector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "quality_gate.post_pr_ci.duration");

        _postPrCiDuration.Record(95.0, PipelineTelemetry.BuildTags(PipelineRunType.Implementation, "proj-1", "TestProj"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => Math.Abs(m.Value - 95.0) < 0.001);
    }

    [Fact]
    public void QualityGateNames_AreStableConstants()
    {
        PipelineTelemetry.QualityGateNames.Compilation.Should().Be("compilation");
        PipelineTelemetry.QualityGateNames.Tests.Should().Be("tests");
        PipelineTelemetry.QualityGateNames.Security.Should().Be("security");
        PipelineTelemetry.QualityGateNames.ExternalCi.Should().Be("external_ci");
    }
}
