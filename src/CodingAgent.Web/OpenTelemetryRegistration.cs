using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodingAgent.Web;

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
        string? redisConnectionString)
    {
        // Same sources as the other hosts and the Serilog OTLP sink, so traces, metrics and logs carry
        // one service.name, and service.version is the image's git SHA rather than the assembly version.
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-web",
                serviceVersion: Environment.GetEnvironmentVariable("SERVICE_VERSION")
                    ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(opts =>
                    opts.Filter = OtelNoiseFilter.FilterAspNetCoreRequest)
                    .AddHttpClientInstrumentation(opts =>
                        opts.FilterHttpRequestMessage = OtelNoiseFilter.FilterHttpClientRequest)
                    .AddSource(PipelineTelemetry.SourceName)
                    .AddSource("Microsoft.AspNetCore.SignalR.Server")
                    .AddProcessor(new OtelNoiseSpanProcessor())
                    .AddOtlpExporter();

                // Redis backplane: trace Redis commands
                if (!string.IsNullOrEmpty(redisConnectionString))
                    t.AddSource("StackExchange.Redis");
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(PipelineTelemetry.SourceName)
                    // .NET runtime metrics (GC, thread pool, CPU, memory) — built-in since .NET 8.
                    .AddMeter("System.Runtime")
                    // Drop low-value built-in ASP.NET Core metrics to reduce OTLP cardinality.
                    // aspnetcore.components.active_circuits (circuit count) is intentionally kept;
                    // the other components.* and memory_pool.* instruments are high-volume noise.
                    .AddView("aspnetcore.components.parameters.count", MetricStreamConfiguration.Drop)
                    .AddView("aspnetcore.components.wrote_to_client", MetricStreamConfiguration.Drop)
                    .AddView("aspnetcore.memory_pool.allocated_bytes", MetricStreamConfiguration.Drop)
                    .AddView("aspnetcore.memory_pool.total_allocated_bytes", MetricStreamConfiguration.Drop)
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

                // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
                // Not registered in the agent — agent pods must not emit these series.
                m.AddMeter(GitHubTelemetry.MeterName);
            });

        return services;
    }
}
