using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Orchestration.Telemetry;
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

                // TODO [WARNING]: WorkDistributionTelemetry meter is added after AddOtlpExporter above.
                // In the OTel .NET SDK, AddMeter and AddOtlpExporter both operate on the same MeterProviderBuilder
                // pipeline — registration order within WithMetrics does not affect which meters are covered by
                // the exporter. This meter IS exported with Cumulative temporality. However, the ordering may
                // mislead future maintainers into thinking it is on a separate, unconfigured exporter. Consider
                // moving AddMeter(WorkDistributionTelemetry.MeterName) inside the fluent chain above, or keep
                // this comment as a clarification. (DotNetSpecialist / Correctness review finding)

                // Work distribution metrics (035a)
                if (!string.IsNullOrEmpty(dbConnectionString))
                    m.AddMeter(WorkDistributionTelemetry.MeterName);
            });

        return services;
    }
}
