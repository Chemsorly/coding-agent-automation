using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for configuring OpenTelemetry tracing and metrics.
/// </summary>
internal static class ObservabilityRegistration
{
    /// <summary>
    /// Adds OpenTelemetry with tracing sources (ASP.NET Core, HTTP, Pipeline, SignalR, optional Npgsql/Redis)
    /// and metrics meters (ASP.NET Core, HTTP, Pipeline, optional WorkDistribution).
    /// </summary>
    public static IServiceCollection AddObservability(this IServiceCollection services, string? dbConnectionString, IConfiguration configuration)
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
                var redisConn = configuration.GetValue<string>("SignalR:Redis:ConnectionString");
                if (!string.IsNullOrEmpty(redisConn))
                    t.AddSource("StackExchange.Redis");
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(PipelineTelemetry.SourceName)
                    .AddOtlpExporter();

                // Work distribution metrics (035a)
                if (!string.IsNullOrEmpty(dbConnectionString))
                    m.AddMeter(CodingAgentWebUI.Orchestration.Telemetry.WorkDistributionTelemetry.MeterName);
            });

        return services;
    }
}
