using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using OpenTelemetry.Metrics;

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
    /// <remarks>
    /// The <c>service.version</c> resource attribute falls back to <c>"local"</c> when
    /// <c>SERVICE_VERSION</c> is not set (previously fell back to the assembly version string).
    /// This is an intentional behaviour change to align with the other hosts (issue #3446).
    /// </remarks>
    internal static IServiceCollection AddApplicationTelemetry(
        this IServiceCollection services,
        string? redisConnectionString)
    {
        services.AddHostOpenTelemetry(
            "coding-agent-web",
            configureTracing: t =>
            {
                // AgentHub spans (RegisterAgent, JobAccepted, etc.) from the SignalR server.
                t.AddSource("Microsoft.AspNetCore.SignalR.Server");

                // Redis backplane: trace Redis commands when a connection string is present.
                if (!string.IsNullOrEmpty(redisConnectionString))
                    t.AddSource("StackExchange.Redis");
            },
            configureMetrics: m => m
                // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
                // Not registered in the agent — agent pods must not emit these series.
                .AddMeter(GitHubTelemetry.MeterName)
                // Drop low-value built-in ASP.NET Core metrics to reduce OTLP cardinality.
                // aspnetcore.components.active_circuits (circuit count) is intentionally kept;
                // the other components.* and memory_pool.* instruments are high-volume noise.
                .AddView("aspnetcore.components.parameters.count", MetricStreamConfiguration.Drop)
                .AddView("aspnetcore.components.wrote_to_client", MetricStreamConfiguration.Drop)
                .AddView("aspnetcore.memory_pool.allocated_bytes", MetricStreamConfiguration.Drop)
                .AddView("aspnetcore.memory_pool.total_allocated_bytes", MetricStreamConfiguration.Drop));

        return services;
    }
}
