using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="HostTelemetry.AddHostOpenTelemetry"/> and
/// <see cref="HostTelemetry.ApplyHostDefaults"/>.
/// </summary>
/// <remarks>
/// Uses <c>[Collection("EnvironmentVariables")]</c> to serialise against other tests that
/// mutate <c>OTEL_SERVICE_NAME</c> and <c>SERVICE_VERSION</c>, preventing cross-test
/// contamination of the process-wide environment.
/// </remarks>
// TODO: [WARNING] This class should also join [Collection("Metrics")] (or a combined collection)
// because AddHostOpenTelemetry_ExportsWorkDistributionAndPipelineMeters records measurements on
// the process-global static WorkDistributionTelemetry.Meter and PipelineTelemetry.Meter.
// WorkDistributionTelemetryDispatchAttemptsTests and WorkDistributionTelemetryCredentialPoolGaugeTests
// in the same assembly use [Collection("Metrics")] to avoid cross-test pollution on those same
// static meters. Running this test in parallel with those collections risks spurious passes or
// unexpected ConcurrentBag entries in their recordings. (TestQualityReviewer, issue #3446)
[Collection("EnvironmentVariables")]
public sealed class HostTelemetryTests : IDisposable
{
    private readonly string? _originalServiceName;
    private readonly string? _originalServiceVersion;

    public HostTelemetryTests()
    {
        _originalServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        _originalServiceVersion = Environment.GetEnvironmentVariable("SERVICE_VERSION");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", _originalServiceName);
        Environment.SetEnvironmentVariable("SERVICE_VERSION", _originalServiceVersion);
    }

    // ── Tracing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The shared helper must subscribe to <see cref="PipelineTelemetry.SourceName"/> so that
    /// pipeline spans are exported in every host process.
    /// </summary>
    [Fact]
    public void AddHostOpenTelemetry_ExportsSpansFromPipelineSource()
    {
        var exporter = new CapturingActivityExporter();

        var services = new ServiceCollection();
        services.AddHostOpenTelemetry("test-service",
            includeAspNetCoreInstrumentation: false,
            includeMetrics: false);
        services.ConfigureOpenTelemetryTracerProvider(b =>
            b.AddProcessor(new SimpleActivityExportProcessor(exporter)));

        using var provider = services.BuildServiceProvider();
        // Resolve TracerProvider to trigger provider construction.
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using var source = new ActivitySource(PipelineTelemetry.SourceName);
        using var activity = source.StartActivity("TestPipelineSpan");
        activity?.Stop();

        tracerProvider.ForceFlush();

        exporter.Exported.Should().Contain(a => a.DisplayName == "TestPipelineSpan",
            "AddHostOpenTelemetry must subscribe to PipelineTelemetry.SourceName so pipeline " +
            "spans are exported from every host process");
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The shared helper must register <see cref="WorkDistributionTelemetry.MeterName"/> and
    /// <see cref="PipelineTelemetry.SourceName"/> so that those meter's instruments are exported.
    /// </summary>
    [Fact]
    public void AddHostOpenTelemetry_ExportsWorkDistributionAndPipelineMeters()
    {
        var exporter = new CapturingMetricExporter();

        var services = new ServiceCollection();
        services.AddHostOpenTelemetry("test-service",
            includeAspNetCoreInstrumentation: false);
        services.ConfigureOpenTelemetryMeterProvider(b =>
            b.AddReader(new BaseExportingMetricReader(exporter)));

        using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        using var wdMeter = new Meter(WorkDistributionTelemetry.MeterName + ".Test." + Guid.NewGuid());
        var wdCounter = wdMeter.CreateCounter<long>("test.workdistribution.counter");

        using var plMeter = new Meter(PipelineTelemetry.SourceName + ".Test." + Guid.NewGuid());
        var plCounter = plMeter.CreateCounter<long>("test.pipeline.counter");

        // TODO: [WARNING] The locals wdMeter/wdCounter and plMeter/plCounter above are dead code —
        // no measurement is recorded on them and they are never asserted. They were intended as an
        // isolated-meter approach but abandoned (the unique GUID names are not registered via
        // AddMeter, so no subscription would capture them). The test instead falls back to recording
        // on the process-global static instances below. The dead locals should be removed to avoid
        // misleading future readers. Additionally, the assertion is weak: it relies on the static
        // meters already being active for any reason, not just because AddHostOpenTelemetry
        // registered them — if a parallel test subscribes those meters independently the assertion
        // would pass even with AddMeter calls removed. Consider replacing with an approach that
        // subscribes freshly-named instruments under AddMeter calls scoped to the test provider.
        // (TestQualityReviewer, issue #3446)

        // Register the test meters with the provider via ConfigureOpenTelemetryMeterProvider
        // before reading — we need explicit AddMeter calls for the unique meter names.
        // Instead, assert on the shared meters that AddHostOpenTelemetry registered directly.
        // WorkDistributionTelemetry.Meter and PipelineTelemetry.Meter are the static instances.
        WorkDistributionTelemetry.Meter.CreateCounter<long>("test.wd.export.counter").Add(1);
        PipelineTelemetry.Meter.CreateCounter<long>("test.pl.export.counter").Add(1);

        meterProvider.ForceFlush();

        // If the meters are registered, the counters on them are collected and exported.
        exporter.PointsFor("test.wd.export.counter").Should().NotBeEmpty(
            "WorkDistributionTelemetry.MeterName must be registered by AddHostOpenTelemetry " +
            "so that workdistribution.* instruments are exported from host processes");
        exporter.PointsFor("test.pl.export.counter").Should().NotBeEmpty(
            "PipelineTelemetry.SourceName must be registered as a meter by AddHostOpenTelemetry " +
            "so that pipeline.* instruments are exported from host processes");
    }

    // ── Resource attributes ───────────────────────────────────────────────────

    /// <summary>
    /// When neither <c>OTEL_SERVICE_NAME</c> nor <c>SERVICE_VERSION</c> is set, the helper
    /// must use the caller-supplied fallback name and the literal string <c>"local"</c>.
    /// </summary>
    [Fact]
    public void AddHostOpenTelemetry_ResourceUsesFallbackNameAndLocalVersion()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", null);
        Environment.SetEnvironmentVariable("SERVICE_VERSION", null);

        var services = new ServiceCollection();
        services.AddHostOpenTelemetry("my-fallback-service",
            includeAspNetCoreInstrumentation: false,
            includeMetrics: false);

        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        var attributes = tracerProvider.GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].Should().Be("my-fallback-service",
            "when OTEL_SERVICE_NAME is not set the fallbackServiceName parameter must be used");
        attributes["service.version"].Should().Be("local",
            "when SERVICE_VERSION is not set the version must default to 'local' to match " +
            "the rule in SerilogOtlpExtensions.WriteToOtlpIfConfigured");
    }

    /// <summary>
    /// When <c>OTEL_SERVICE_NAME</c> and <c>SERVICE_VERSION</c> are set, those values must
    /// take precedence over the fallback name and the "local" default.
    /// </summary>
    [Fact]
    public void AddHostOpenTelemetry_ResourcePrefersEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "env-service");
        Environment.SetEnvironmentVariable("SERVICE_VERSION", "1.2.3");

        var services = new ServiceCollection();
        services.AddHostOpenTelemetry("fallback-service",
            includeAspNetCoreInstrumentation: false,
            includeMetrics: false);

        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        var attributes = tracerProvider.GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].Should().Be("env-service",
            "OTEL_SERVICE_NAME env var must take precedence over the fallback service name");
        attributes["service.version"].Should().Be("1.2.3",
            "SERVICE_VERSION env var must take precedence over the 'local' default");
    }

    // ── Serilog overrides ─────────────────────────────────────────────────────

    /// <summary>
    /// The five noisy framework namespaces must be raised to <c>Warning</c> regardless of
    /// the process minimum level. This verifies that a <c>Debug</c> event from each
    /// namespace is dropped while a <c>Warning</c> event from the same namespace is kept.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Polly")]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("Microsoft.Extensions.Http")]
    [InlineData("OpenTelemetry")]
    public void ApplyHostDefaults_RaisesFrameworkCategoriesToWarning(string category)
    {
        var sink = new CollectingSink();

        var logger = new LoggerConfiguration()
            .ApplyHostDefaults(LogEventLevel.Debug)
            .WriteTo.Sink(sink)
            .CreateLogger();

        // ForContext with SourceContext to simulate events from the given namespace.
        var categoryLogger = logger.ForContext("SourceContext", category);

        categoryLogger.Debug("Debug event — should be suppressed");
        categoryLogger.Warning("Warning event — should be kept");

        logger.Dispose();

        var debugEvents = sink.Events
            .Where(e => e.Level == LogEventLevel.Debug)
            .ToList();
        var warningEvents = sink.Events
            .Where(e => e.Level == LogEventLevel.Warning)
            .ToList();

        debugEvents.Should().BeEmpty(
            $"ApplyHostDefaults must suppress Debug events from '{category}' " +
            "by raising its minimum level to Warning");
        warningEvents.Should().ContainSingle(
            $"ApplyHostDefaults must pass Warning events from '{category}' " +
            "through to the sink");
    }

    /// <summary>
    /// For namespaces not in the framework override list, the process minimum level governs.
    /// </summary>
    [Fact]
    public void ApplyHostDefaults_AppliesMinimumLevelToOtherCategories()
    {
        var debugSink = new CollectingSink();
        var debugLogger = new LoggerConfiguration()
            .ApplyHostDefaults(LogEventLevel.Debug)
            .WriteTo.Sink(debugSink)
            .CreateLogger();

        var infoSink = new CollectingSink();
        var infoLogger = new LoggerConfiguration()
            .ApplyHostDefaults(LogEventLevel.Information)
            .WriteTo.Sink(infoSink)
            .CreateLogger();

        var debugAppLogger = debugLogger.ForContext("SourceContext", "CodingAgent.Test");
        var infoAppLogger = infoLogger.ForContext("SourceContext", "CodingAgent.Test");

        debugAppLogger.Debug("Debug at Debug minimum — should be kept");
        infoAppLogger.Debug("Debug at Information minimum — should be dropped");

        debugLogger.Dispose();
        infoLogger.Dispose();

        debugSink.Events.Should().ContainSingle(e => e.Level == LogEventLevel.Debug,
            "a Debug event from a non-framework category must pass when minimum level is Debug");
        infoSink.Events.Should().BeEmpty(
            "a Debug event from a non-framework category must be dropped when minimum level is Information");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Thread-safe Serilog sink that collects log events for assertion.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_lock) { return [.. _events]; } }
        }

        public void Emit(LogEvent logEvent) { lock (_lock) { _events.Add(logEvent); } }
    }
}
