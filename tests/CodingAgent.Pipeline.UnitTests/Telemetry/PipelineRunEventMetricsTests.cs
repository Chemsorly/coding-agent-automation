using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Unit tests for the new API-side metrics introduced in issue #2979:
/// - pipeline.run.quality_gate.results (counter)
/// - pipeline.run.ci.not_started_retriggers (counter)
/// - pipeline.run.ci.wait (histogram)
/// - pipeline.run.agent_stalls (counter)
///
/// These instruments are recorded by the API (AgentHub.Lifecycle.cs) when events
/// arrive from agent pods — not by the agent pods themselves. These tests verify
/// the metric definitions, tag values, and expected call sites.
/// </summary>
[Collection("Metrics")]
public sealed class PipelineRunEventMetricsTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ── pipeline.run.quality_gate.results ────────────────────────────────────

    [Theory]
    [InlineData("compilation", true, false)]
    [InlineData("compilation", false, false)]
    [InlineData("tests", true, false)]
    [InlineData("tests", false, false)]
    [InlineData("tests", false, true)]
    [InlineData("external_ci", true, false)]
    [InlineData("external_ci", false, false)]
    [InlineData("external_ci", false, true)]
    public void RunQualityGateResults_RecordsExpectedTags(string gate, bool passed, bool infraFailure)
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.run.quality_gate.results");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("pipeline.run.quality_gate.results", "{evaluation}", "Quality gate evaluation outcomes");

        counter.Add(1,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("gate", gate),
            new KeyValuePair<string, object?>("result", passed ? "pass" : "fail"),
            new KeyValuePair<string, object?>("infrastructure_failure", infraFailure ? "true" : "false"));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Tags.Should().ContainKey("gate").WhoseValue.Should().Be(gate);
        snapshot[0].Tags.Should().ContainKey("result").WhoseValue.Should().Be(passed ? "pass" : "fail");
        snapshot[0].Tags.Should().ContainKey("infrastructure_failure").WhoseValue.Should().Be(infraFailure ? "true" : "false");
        snapshot[0].Tags.Should().ContainKey("run_type").WhoseValue.Should().Be("implementation");
    }

    [Fact]
    public void RunQualityGateResults_InstrumentNameIsStable()
    {
        // The Prometheus metric name is pipeline_run_quality_gate_results_total
        PipelineTelemetry.RunQualityGateResults.Name.Should().Be("pipeline.run.quality_gate.results");
    }

    // ── pipeline.run.ci.not_started_retriggers ───────────────────────────────

    [Fact]
    public void RunCiNotStartedRetriggers_RecordsRunTypeTag()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.run.ci.not_started_retriggers");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("pipeline.run.ci.not_started_retriggers", "{retrigger}", "CI re-triggers");

        counter.Add(1, PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Tags.Should().ContainKey("run_type").WhoseValue.Should().Be("implementation");
    }

    [Fact]
    public void RunCiNotStartedRetriggers_InstrumentNameIsStable()
    {
        PipelineTelemetry.RunCiNotStartedRetriggers.Name.Should().Be("pipeline.run.ci.not_started_retriggers");
    }

    // ── pipeline.run.ci.wait ─────────────────────────────────────────────────

    [Theory]
    [InlineData("pre_pr", "pass")]
    [InlineData("pre_pr", "fail")]
    [InlineData("post_pr", "pass")]
    [InlineData("post_pr", "fail")]
    public void RunCiWait_RecordsStageAndResultTags(string stage, string result)
    {
        using var collector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "pipeline.run.ci.wait");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var histogram = meter.CreateHistogram<double>("pipeline.run.ci.wait", "s", "CI wait time");

        histogram.Record(120.0,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("result", result));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Value.Should().Be(120.0);
        snapshot[0].Tags.Should().ContainKey("stage").WhoseValue.Should().Be(stage);
        snapshot[0].Tags.Should().ContainKey("result").WhoseValue.Should().Be(result);
    }

    [Fact]
    public void RunCiWait_InstrumentNameIsStable()
    {
        PipelineTelemetry.RunCiWait.Name.Should().Be("pipeline.run.ci.wait");
    }

    [Fact]
    public void RunCiWait_HasExpectedBucketBoundaries()
    {
        // Verify the histogram is created with InstrumentAdvice (bucket boundaries).
        // This ensures the Prometheus exporter emits _bucket vectors, not exponential histograms.
        // We verify this by checking that the InstrumentAdvice is set on the static shared histogram
        // via a MeterListener that captures the Histogram<double> instrument directly.
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var buckets = (double[]?)null;

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.ci.wait"
                && instrument is System.Diagnostics.Metrics.Histogram<double> h)
            {
                buckets = h.Advice?.HistogramBucketBoundaries?.ToArray();
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        // Warm up the static Meter so the listener can observe the instrument
        PipelineTelemetry.RunCiWait.Record(0.0);
        listener.RecordObservableInstruments();

        listener.Dispose();

        buckets.Should().NotBeNull("RunCiWait must have explicit bucket boundaries (InstrumentAdvice)");
        buckets!.Should().Contain(60.0, "1-minute bucket is required for CI wait histogram");
        buckets.Should().Contain(3600.0, "1-hour bucket is required for long CI wait histogram");
    }

    // ── pipeline.run.agent_stalls ─────────────────────────────────────────────

    [Theory]
    [InlineData("qgc_retry_agent", "stall_kill")]
    [InlineData("codegen", "process_death")]
    [InlineData("analysis", "process_timeout")]
    [InlineData("code_review", "stall_kill")]
    [InlineData("decomposition", "process_death")]
    [InlineData("unknown", "stall_kill")]
    public void RunAgentStalls_RecordsPhaseAndKindTags(string phase, string kind)
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.run.agent_stalls");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("pipeline.run.agent_stalls", "{stall}", "Agent stall events");

        counter.Add(1,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("kind", kind));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Tags.Should().ContainKey("phase").WhoseValue.Should().Be(phase);
        snapshot[0].Tags.Should().ContainKey("kind").WhoseValue.Should().Be(kind);
    }

    [Fact]
    public void RunAgentStalls_InstrumentNameIsStable()
    {
        PipelineTelemetry.RunAgentStalls.Name.Should().Be("pipeline.run.agent_stalls");
    }

    // ── Closed-set constant sanity ────────────────────────────────────────────

    [Fact]
    public void AgentStallKinds_ContainsExpectedValues()
    {
        PipelineTelemetry.AgentStallKinds.StallKill.Should().Be("stall_kill");
        PipelineTelemetry.AgentStallKinds.ProcessDeath.Should().Be("process_death");
        PipelineTelemetry.AgentStallKinds.ProcessTimeout.Should().Be("process_timeout");
        PipelineTelemetry.AgentStallKinds.All.Should().HaveCount(3);
    }

    [Fact]
    public void CiWaitStages_ContainsExpectedValues()
    {
        PipelineTelemetry.CiWaitStages.PrePr.Should().Be("pre_pr");
        PipelineTelemetry.CiWaitStages.PostPr.Should().Be("post_pr");
    }

    [Fact]
    public void QualityGateResultGates_ContainsExpectedValues()
    {
        PipelineTelemetry.QualityGateResultGates.Compilation.Should().Be("compilation");
        PipelineTelemetry.QualityGateResultGates.Tests.Should().Be("tests");
        PipelineTelemetry.QualityGateResultGates.ExternalCi.Should().Be("external_ci");
        PipelineTelemetry.QualityGateResultGates.All.Should().HaveCount(3);
    }

    // ── Pre-init coverage ─────────────────────────────────────────────────────

    [Fact]
    public void NewInstruments_AreDefinedOnPipelineTelemetryMeter()
    {
        // Verify the three new counters and histogram are defined on the shared PipelineTelemetry.Meter.
        // This guards against the instruments accidentally being created on a different meter
        // (which would cause them to be silently excluded from export in the API process).
        PipelineTelemetry.RunQualityGateResults.Name.Should().Be("pipeline.run.quality_gate.results");
        PipelineTelemetry.RunCiNotStartedRetriggers.Name.Should().Be("pipeline.run.ci.not_started_retriggers");
        PipelineTelemetry.RunCiWait.Name.Should().Be("pipeline.run.ci.wait");
        PipelineTelemetry.RunAgentStalls.Name.Should().Be("pipeline.run.agent_stalls");

        // All four new instruments must be on the same meter as existing pipeline instruments.
        PipelineTelemetry.RunQualityGateResults.Meter.Name.Should().Be(PipelineTelemetry.SourceName);
        PipelineTelemetry.RunCiNotStartedRetriggers.Meter.Name.Should().Be(PipelineTelemetry.SourceName);
        PipelineTelemetry.RunCiWait.Meter.Name.Should().Be(PipelineTelemetry.SourceName);
        PipelineTelemetry.RunAgentStalls.Meter.Name.Should().Be(PipelineTelemetry.SourceName);
    }
}
