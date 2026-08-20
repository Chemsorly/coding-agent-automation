using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering OpenTelemetry tracing and metrics.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
internal static class OpenTelemetryRegistration
{
    /// <summary>
    /// Adds OpenTelemetry tracing and metrics with OTLP export.
    /// </summary>
    internal static IServiceCollection AddApplicationTelemetry(
        this IServiceCollection services,
        string? dbConnectionString,
        string? redisConnectionString)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: "coding-agent-orchestrator",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(PipelineTelemetry.SourceName)
                    .AddSource("Microsoft.AspNetCore.SignalR.Server")
                    .AddOtlpExporter();

                // DB mode: Npgsql tracing for query spans
                if (!string.IsNullOrEmpty(dbConnectionString))
                    t.AddSource("Npgsql");

                // Redis backplane: trace Redis commands
                if (!string.IsNullOrEmpty(redisConnectionString))
                    t.AddSource("StackExchange.Redis");
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(PipelineTelemetry.SourceName)
                    // Prometheus requires Cumulative temporality. The OTLP exporter defaults to Delta
                    // for histograms and counters, which causes Grafana Cloud to silently drop histogram
                    // data (dispatch_queue_wait_time, pipeline_jobs_duration, etc.) while gauges — which
                    // have no temporality — continue to export correctly. Setting Cumulative here ensures
                    // all instrument types are compatible with the Prometheus remote-write pipeline.
                    .AddOtlpExporter((_, readerOptions) =>
                        readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative);

                // Work distribution metrics (035a).
                //
                // Unconditional since Spec 045: the gate used to be
                // `if (!string.IsNullOrEmpty(dbConnectionString))`, but that spec removed the
                // monolith's database connection, making the gate permanently false and silently
                // un-exporting every workdistribution.* instrument this process still records.
                // The bulk of these instruments now live in the Pipeline API, which registers the
                // same meter; the monolith keeps its own registration for what it still emits.
                //
                // AddMeter after AddOtlpExporter is fine — both operate on the same
                // MeterProviderBuilder, so this meter is exported with Cumulative temporality too.
                m.AddMeter(WorkDistributionTelemetry.MeterName);
            });

        return services;
    }
}
