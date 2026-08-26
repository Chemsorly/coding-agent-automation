using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace CodingAgentWebUI.JobController;

/// <summary>
/// Serilog OTLP extension for the Job Controller.
/// Mirrors the Infrastructure.Telemetry.SerilogOtlpExtensions pattern but avoids
/// referencing CodingAgentWebUI.Infrastructure (which would pull in EF Core).
/// </summary>
internal static class JobControllerSerilogExtensions
{
    /// <summary>
    /// Conditionally adds the OpenTelemetry OTLP sink if OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// </summary>
    public static LoggerConfiguration WriteToOtlpIfConfigured(
        this LoggerConfiguration lc,
        string serviceName,
        string? environmentName = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return lc;

        environmentName ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                         ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                         ?? "Production";

        return lc.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = OtlpProtocol.Grpc;
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName,
                ["deployment.environment"] = environmentName
            };
        }, ignoreEnvironment: true);
    }
}
