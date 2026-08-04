using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Regression tests verifying that all four quality_gate.* metric instruments are registered
/// under <see cref="PipelineTelemetry.SourceName"/>.
///
/// Background: The worker agent's Program.cs registers
/// <c>.AddMeter(PipelineTelemetry.SourceName)</c> in the OTel SDK configuration.
/// This is the mechanism that causes the OTel SDK to observe and export quality gate metrics.
/// If any quality gate instrument were defined on a different meter, it would be silently
/// skipped by the OTLP exporter.
///
/// These tests guard against:
/// 1. Moving a quality gate instrument to a different Meter instance.
/// 2. Renaming <see cref="PipelineTelemetry.SourceName"/> without updating the agent Program.cs.
/// 3. Accidentally creating new quality gate instruments on a different meter.
/// </summary>
[Collection("Metrics")]
public sealed class QualityGateMetricsMeterRegistrationTests : IDisposable
{
    private readonly MeterListener _listener = new();
    // Use ConcurrentBag<T> to match the established pattern in PipelineTelemetryQualityGateTests,
    // PipelineRunInstrumentationTests, StepMetricsTests, and other metric tests in this suite.
    // MeterListener measurement callbacks can fire on a different thread; List<T> is not thread-safe
    // and caused "Collection was modified; enumeration operation may not execute." in CI.
    private readonly ConcurrentBag<(string InstrumentName, string MeterName)> _observed = [];

    public QualityGateMetricsMeterRegistrationTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
            listener.EnableMeasurementEvents(instrument);

        _listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
            _observed.Add((instrument.Name, instrument.Meter.Name)));

        _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
            _observed.Add((instrument.Name, instrument.Meter.Name)));

        _listener.Start();

        // Force-observe all four quality gate instruments by emitting warm-up measurements.
        // MeterListener may miss instruments created before Start() unless we trigger them.
        PipelineTelemetry.QualityGateRetries.Add(0);
        PipelineTelemetry.QualityGateEvaluations.Add(0);
        PipelineTelemetry.QualityGateDuration.Record(0.0);
        PipelineTelemetry.ExternalCiDuration.Record(0.0);

        _observed.Clear();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// quality_gate_retries_total must be on PipelineTelemetry.SourceName so that
    /// AddMeter(PipelineTelemetry.SourceName) in the agent's Program.cs includes it.
    /// </summary>
    [Fact]
    public void QualityGateRetries_IsOnPipelineTelemetryMeter()
    {
        PipelineTelemetry.QualityGateRetries.Add(1);

        var entry = _observed.FirstOrDefault(o => o.InstrumentName == "quality_gate.retries");
        entry.MeterName.Should().Be(PipelineTelemetry.SourceName,
            "quality_gate.retries must be defined on the PipelineTelemetry meter. " +
            "The agent's Program.cs calls AddMeter(PipelineTelemetry.SourceName) to enable OTLP export. " +
            "If this instrument moves to a different meter it will be silently skipped by the exporter.");
    }

    /// <summary>
    /// quality_gate_evaluations_total must be on PipelineTelemetry.SourceName.
    /// </summary>
    [Fact]
    public void QualityGateEvaluations_IsOnPipelineTelemetryMeter()
    {
        PipelineTelemetry.QualityGateEvaluations.Add(1);

        var entry = _observed.FirstOrDefault(o => o.InstrumentName == "quality_gate.evaluations");
        entry.MeterName.Should().Be(PipelineTelemetry.SourceName,
            "quality_gate.evaluations must be defined on the PipelineTelemetry meter. " +
            "The agent's Program.cs calls AddMeter(PipelineTelemetry.SourceName) to enable OTLP export. " +
            "If this instrument moves to a different meter it will be silently skipped by the exporter.");
    }

    /// <summary>
    /// quality_gate_duration_seconds must be on PipelineTelemetry.SourceName.
    /// </summary>
    [Fact]
    public void QualityGateDuration_IsOnPipelineTelemetryMeter()
    {
        PipelineTelemetry.QualityGateDuration.Record(1.0);

        var entry = _observed.FirstOrDefault(o => o.InstrumentName == "quality_gate.duration");
        entry.MeterName.Should().Be(PipelineTelemetry.SourceName,
            "quality_gate.duration must be defined on the PipelineTelemetry meter. " +
            "The agent's Program.cs calls AddMeter(PipelineTelemetry.SourceName) to enable OTLP export. " +
            "If this instrument moves to a different meter it will be silently skipped by the exporter.");
    }

    /// <summary>
    /// quality_gate_external_ci_duration_seconds must be on PipelineTelemetry.SourceName.
    /// </summary>
    [Fact]
    public void ExternalCiDuration_IsOnPipelineTelemetryMeter()
    {
        PipelineTelemetry.ExternalCiDuration.Record(1.0);

        var entry = _observed.FirstOrDefault(o => o.InstrumentName == "quality_gate.external_ci.duration");
        entry.MeterName.Should().Be(PipelineTelemetry.SourceName,
            "quality_gate.external_ci.duration must be defined on the PipelineTelemetry meter. " +
            "The agent's Program.cs calls AddMeter(PipelineTelemetry.SourceName) to enable OTLP export. " +
            "If this instrument moves to a different meter it will be silently skipped by the exporter.");
    }

    /// <summary>
    /// All four quality gate instruments must use the exact metric names that the Grafana
    /// dashboard "Coding Agent Pipeline — Quality Gates" queries. Changing these names
    /// would break the dashboard silently.
    /// </summary>
    [Fact]
    public void AllFourQualityGateInstruments_HaveCanonicalNames()
    {
        // Trigger measurements to confirm names
        PipelineTelemetry.QualityGateRetries.Add(1);
        PipelineTelemetry.QualityGateEvaluations.Add(1);
        PipelineTelemetry.QualityGateDuration.Record(1.0);
        PipelineTelemetry.ExternalCiDuration.Record(1.0);

        var instrumentNames = _observed.Select(o => o.InstrumentName).Distinct().ToList();

        instrumentNames.Should().Contain("quality_gate.retries",
            "Grafana dashboard queries quality_gate_retries_total");
        instrumentNames.Should().Contain("quality_gate.evaluations",
            "Grafana dashboard queries quality_gate_evaluations_total");
        instrumentNames.Should().Contain("quality_gate.duration",
            "Grafana dashboard queries quality_gate_duration_seconds");
        instrumentNames.Should().Contain("quality_gate.external_ci.duration",
            "Grafana dashboard queries quality_gate_external_ci_duration_seconds");
    }

    /// <summary>
    /// The agent's Program.cs must register PipelineTelemetry.SourceName as a meter
    /// with the OTLP exporter. This is the production wiring that enables quality gate
    /// metrics to flow to Prometheus/Grafana from ephemeral worker pods.
    /// </summary>
    [Fact]
    public void AgentProgramCs_RegistersPipelineTelemetryMeterWithOtlpExporter()
    {
        var agentProgramPath = FindSourceFile("src/CodingAgentWebUI.Agent/Program.cs");
        var sourceCode = File.ReadAllText(agentProgramPath);

        sourceCode.Should().Contain($"AddMeter(PipelineTelemetry.SourceName)",
            "The agent's Program.cs must call AddMeter(PipelineTelemetry.SourceName) in the " +
            "WithMetrics() configuration block. Without this, the OTel SDK will not observe or " +
            "export any quality_gate.* instruments even when OTLP_ENDPOINT is configured.");

        sourceCode.Should().Contain("AddOtlpExporter",
            "The agent's Program.cs must call AddOtlpExporter() in the WithMetrics() " +
            "configuration block to push quality_gate.* metrics to the OTLP endpoint. " +
            "Without OTLP export, ephemeral worker pods cannot deliver metrics to Prometheus.");

        sourceCode.Should().Contain("MetricReaderTemporalityPreference.Cumulative",
            "The OTLP exporter must use Cumulative temporality. The SDK defaults to Delta, " +
            "which Prometheus/Grafana Cloud silently drops for histograms and counters. " +
            "Cumulative is required for quality_gate_retries_total and quality_gate_evaluations_total " +
            "to appear as monotonically increasing counters in Grafana.");
    }

    private static string FindSourceFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new InvalidOperationException("Could not find solution root");
        return Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
