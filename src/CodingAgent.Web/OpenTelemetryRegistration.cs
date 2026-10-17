using CodingAgent.Infrastructure.Telemetry;

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
    /// The web host hosts no SignalR hub and renders no circuits (spec 052), so it adds no source or
    /// metric view of its own: forwarded calls show up in the shared ASP.NET Core and HttpClient
    /// instrumentation.
    /// </remarks>
    internal static IServiceCollection AddApplicationTelemetry(this IServiceCollection services)
    {
        services.AddHostOpenTelemetry("coding-agent-web");

        return services;
    }
}
