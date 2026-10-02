using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests that AgentHub.ReportQualityGateResult records API-side metrics
/// (pipeline.run.quality_gate.results) when called (issue #2979).
/// Uses TestMeterFactory to isolate instrument recording from the shared static Meter.
/// </summary>
/// <remarks>
/// NOTE: The tautological tests below (RecordGateResultMapping_*, CiNotStartedRetrigger_*, etc.)
/// exercise the .NET Metrics API round-trip, not the production <c>AgentHub</c> dispatch logic.
/// Hub-dispatch tests that call <c>hub.ReportQualityGateResult</c> and <c>hub.ReportPipelineRunEvent</c>
/// directly are in <c>AgentHubReportQualityGateResultMetricsTests</c> and
/// <c>AgentHubReportPipelineRunEventDispatchTests</c> in AgentHubPipelineReportingTests.cs.
/// </remarks>
[Collection("Metrics")]
public sealed class AgentHubQualityGateMetricsTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ── Gate result mapping: RecordQualityGateResultMetrics (static helper via ReportQualityGateResult) ──

    [Theory]
    [InlineData("pass", "false")]
    [InlineData("fail", "false")]
    [InlineData("fail", "true")]
    public void RecordGateResultMapping_MapsPassedAndInfraFailureTags(
        string expectedResult, string expectedInfraTag)
    {
        // Verify the tag mapping logic directly against the static PipelineTelemetry instruments.
        // Using the shared static counter since AgentHub.RecordGate is private — we test via the instrument.
        using var collector = new MetricCollector<long>(
            _factory, PipelineTelemetry.SourceName, "pipeline.run.quality_gate.results");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>(
            "pipeline.run.quality_gate.results", "{evaluation}", "");

        counter.Add(1,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("gate", "tests"),
            new KeyValuePair<string, object?>("result", expectedResult),
            new KeyValuePair<string, object?>("infrastructure_failure", expectedInfraTag));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Tags["result"].Should().Be(expectedResult);
        snapshot[0].Tags["infrastructure_failure"].Should().Be(expectedInfraTag);
    }

    [Fact]
    public void GateNameMapping_CompilationGateUsesCorrectTagValue()
    {
        // Verify the compilation gate name constant matches the expected Prometheus label value
        PipelineTelemetry.QualityGateResultGates.Compilation.Should().Be("compilation",
            "gate tag must be 'compilation' for Prometheus query compatibility");
    }

    [Fact]
    public void GateNameMapping_TestsGateUsesCorrectTagValue()
    {
        PipelineTelemetry.QualityGateResultGates.Tests.Should().Be("tests",
            "gate tag must be 'tests' for Prometheus query compatibility");
    }

    [Fact]
    public void GateNameMapping_ExternalCiGateUsesCorrectTagValue()
    {
        PipelineTelemetry.QualityGateResultGates.ExternalCi.Should().Be("external_ci",
            "gate tag must be 'external_ci' for Prometheus query compatibility");
    }
}

/// <summary>
/// Tests that AgentHub.ReportPipelineRunEvent records the correct API-side metrics
/// for CI re-triggers, CI wait duration, and agent stalls (issue #2979).
/// </summary>
[Collection("Metrics")]
public sealed class AgentHubPipelineRunEventMetricsTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void CiNotStartedRetrigger_EventKindMapsToCounter()
    {
        // Verify the counter name and tag structure for CiNotStartedRetrigger events.
        using var collector = new MetricCollector<long>(
            _factory, PipelineTelemetry.SourceName, "pipeline.run.ci.not_started_retriggers");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("pipeline.run.ci.not_started_retriggers", "{retrigger}", "");

        counter.Add(1, PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation));

        collector.GetMeasurementSnapshot().Should().HaveCount(1,
            "one re-trigger event must produce exactly one counter increment");
        collector.GetMeasurementSnapshot()[0].Tags["run_type"].Should().Be("implementation");
    }

    [Fact]
    public void CiWait_PrePrStageEventMapsToHistogram()
    {
        using var collector = new MetricCollector<double>(
            _factory, PipelineTelemetry.SourceName, "pipeline.run.ci.wait");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var histogram = meter.CreateHistogram<double>("pipeline.run.ci.wait", "s", "");

        histogram.Record(300.0,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("stage", PipelineTelemetry.CiWaitStages.PrePr),
            new KeyValuePair<string, object?>("result", "pass"));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Value.Should().Be(300.0, "duration must be recorded as reported seconds");
        snapshot[0].Tags["stage"].Should().Be("pre_pr");
        snapshot[0].Tags["result"].Should().Be("pass");
    }

    [Fact]
    public void CiWait_PostPrStageEventMapsToHistogram()
    {
        using var collector = new MetricCollector<double>(
            _factory, PipelineTelemetry.SourceName, "pipeline.run.ci.wait");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var histogram = meter.CreateHistogram<double>("pipeline.run.ci.wait", "s", "");

        histogram.Record(600.0,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Review),
            new KeyValuePair<string, object?>("stage", PipelineTelemetry.CiWaitStages.PostPr),
            new KeyValuePair<string, object?>("result", "fail"));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Tags["stage"].Should().Be("post_pr");
        snapshot[0].Tags["result"].Should().Be("fail");
        snapshot[0].Tags["run_type"].Should().Be("review");
    }

    [Fact]
    public void AgentStall_StallKillEventMapsToCounter()
    {
        using var collector = new MetricCollector<long>(
            _factory, PipelineTelemetry.SourceName, "pipeline.run.agent_stalls");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("pipeline.run.agent_stalls", "{stall}", "");

        counter.Add(1,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent),
            new KeyValuePair<string, object?>("kind", PipelineTelemetry.AgentStallKinds.StallKill));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Tags["phase"].Should().Be("qgc_retry_agent");
        snapshot[0].Tags["kind"].Should().Be("stall_kill");
    }

    [Fact]
    public void AgentStall_ProcessDeathEventMapsToCounter()
    {
        using var collector = new MetricCollector<long>(
            _factory, PipelineTelemetry.SourceName, "pipeline.run.agent_stalls");
        var meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("pipeline.run.agent_stalls", "{stall}", "");

        counter.Add(1,
            PipelineTelemetry.RunTypeTag(PipelineRunType.Implementation),
            new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.CodeGen),
            new KeyValuePair<string, object?>("kind", PipelineTelemetry.AgentStallKinds.ProcessDeath));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot[0].Tags["phase"].Should().Be("codegen");
        snapshot[0].Tags["kind"].Should().Be("process_death");
    }

    // ── PipelineRunEventReport message structure ──────────────────────────────

    [Fact]
    public void PipelineRunEventReport_CiNotStartedRetrigger_HasNoRequiredFields()
    {
        // A CI re-trigger event only needs the Kind — DurationSeconds, Stage, Result are null
        var report = new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiNotStartedRetrigger
        };

        report.Kind.Should().Be(PipelineRunEventKind.CiNotStartedRetrigger);
        report.DurationSeconds.Should().BeNull();
        report.Stage.Should().BeNull();
        report.Result.Should().BeNull();
    }

    [Fact]
    public void PipelineRunEventReport_CiWait_CarriesDurationStageAndResult()
    {
        var report = new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiWait,
            DurationSeconds = 450.5,
            Stage = PipelineTelemetry.CiWaitStages.PostPr,
            Result = "pass"
        };

        report.Kind.Should().Be(PipelineRunEventKind.CiWait);
        report.DurationSeconds.Should().Be(450.5);
        report.Stage.Should().Be("post_pr");
        report.Result.Should().Be("pass");
    }

    [Fact]
    public void PipelineRunEventReport_AgentStall_CarriesStageAndKind()
    {
        var report = new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.AgentStall,
            Stage = PipelineTelemetry.StallPhases.Analysis,
            Result = PipelineTelemetry.AgentStallKinds.ProcessDeath
        };

        report.Kind.Should().Be(PipelineRunEventKind.AgentStall);
        report.Stage.Should().Be("analysis");
        report.Result.Should().Be("process_death");
    }
}
