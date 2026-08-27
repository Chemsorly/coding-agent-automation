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
    /// Reads OTEL_EXPORTER_OTLP_PROTOCOL and OTEL_EXPORTER_OTLP_HEADERS from the environment,
    /// matching the behaviour of Infrastructure.Telemetry.SerilogOtlpExtensions.
    /// </summary>
    public static LoggerConfiguration WriteToOtlpIfConfigured(
        this LoggerConfiguration lc,
        string serviceName,
        string? environmentName = null)
    {
        ArgumentNullException.ThrowIfNull(lc);
        ArgumentNullException.ThrowIfNull(serviceName);
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return lc;

        environmentName ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                         ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                         ?? "Production";

        return lc.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = ParseOtlpProtocol();
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName,
                ["deployment.environment"] = environmentName
            };
            ApplyOtlpHeaders(options);
        }, ignoreEnvironment: true);
    }

    private static OtlpProtocol ParseOtlpProtocol()
    {
        var protocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        if (!string.IsNullOrEmpty(protocol)
            && !string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Unrecognized OTEL_EXPORTER_OTLP_PROTOCOL value '{Protocol}', falling back to gRPC. Expected 'http/protobuf' or 'grpc'", protocol);
        }
        return string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpProtocol.HttpProtobuf
            : OtlpProtocol.Grpc;
    }

    private static void ApplyOtlpHeaders(OpenTelemetrySinkOptions options)
    {
        var headers = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        if (string.IsNullOrWhiteSpace(headers))
            return;

        foreach (var pair in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                Log.Warning("OTEL_EXPORTER_OTLP_HEADERS contains invalid entry '{Entry}' (missing '=' separator), skipping", pair);
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex].Trim());
            if (string.IsNullOrWhiteSpace(key))
            {
                Log.Warning("OTEL_EXPORTER_OTLP_HEADERS contains entry with empty key, skipping");
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            options.Headers[key] = value;
        }
    }
}
