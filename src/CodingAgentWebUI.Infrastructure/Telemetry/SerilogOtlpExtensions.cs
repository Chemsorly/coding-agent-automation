using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace CodingAgentWebUI.Infrastructure.Telemetry;

/// <summary>
/// Extension methods for configuring the Serilog OTLP sink conditionally.
/// The sink is only added when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
/// </summary>
public static class SerilogOtlpExtensions
{
    /// <summary>
    /// Conditionally adds the OpenTelemetry OTLP sink if <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured.
    /// </summary>
    /// <param name="loggerConfiguration">The Serilog logger configuration.</param>
    /// <param name="serviceName">The service name (must match the OTel SDK resource config).</param>
    /// <param name="environmentName">
    /// The deployment environment name. If null, reads from <c>ASPNETCORE_ENVIRONMENT</c>
    /// or <c>DOTNET_ENVIRONMENT</c> env vars, defaulting to "Production".
    /// </param>
    public static LoggerConfiguration WriteToOtlpIfConfigured(
        this LoggerConfiguration loggerConfiguration,
        string serviceName,
        string? environmentName = null)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for loggerConfiguration and serviceName to match codebase conventions
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return loggerConfiguration;

        environmentName ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                         ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                         ?? "Production";

        return loggerConfiguration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = OtlpProtocol.Grpc;
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName,
                ["deployment.environment"] = environmentName
            };

            var headers = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
            // TODO: URL-decode header keys/values per OTEL spec (values may contain %20, %3D, etc.)
            if (!string.IsNullOrWhiteSpace(headers))
            {
                foreach (var pair in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var separatorIndex = pair.IndexOf('=');
                    if (separatorIndex > 0)
                    {
                        var key = pair[..separatorIndex].Trim();
                        var value = pair[(separatorIndex + 1)..];
                        options.Headers[key] = value;
                    }
                }
            }
        });
    }
}
