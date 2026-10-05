using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace CodingAgent.Infrastructure.Telemetry;

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
    /// <param name="fallbackServiceName">
    /// The service name to use when <c>OTEL_SERVICE_NAME</c> is not set.
    /// <c>OTEL_SERVICE_NAME</c> always takes precedence — pass the per-host default here (e.g.
    /// <c>"coding-agent-api"</c>) so it is used in local/test environments where the env var is absent.
    /// </param>
    /// <param name="environmentName">
    /// The host's environment name, used for <c>deployment.environment</c> when
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c> carries none. See <see cref="ResolveDeploymentEnvironment"/>.
    /// </param>
    public static LoggerConfiguration WriteToOtlpIfConfigured(
        this LoggerConfiguration loggerConfiguration,
        string fallbackServiceName,
        string? environmentName = null)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(fallbackServiceName);
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return loggerConfiguration;

        // OTEL_SERVICE_NAME env var takes precedence; fallbackServiceName is used when it is absent.
        // Every host's tracer and meter provider reads the same variable, so logs and traces agree.
        var resolvedServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? fallbackServiceName;
        var resolvedEnvironment = ResolveDeploymentEnvironment(environmentName);

        // ignoreEnvironment: true because we read OTEL env vars ourselves with URL-decoding and validation
        return loggerConfiguration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = ParseOtlpProtocol();
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = resolvedServiceName,
                ["service.version"] = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "local",
                ["deployment.environment"] = resolvedEnvironment
            };

            ApplyOtlpHeaders(options);
        }, ignoreEnvironment: true);
    }

    /// <summary>
    /// Resolves the <c>deployment.environment</c> attribute for the log sink. The value in
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c> wins, because the OTel SDK puts that same value on traces and
    /// metrics; otherwise <paramref name="environmentName"/>, then <c>ASPNETCORE_ENVIRONMENT</c> or
    /// <c>DOTNET_ENVIRONMENT</c>, defaulting to "Production".
    /// </summary>
    /// <param name="environmentName">The host's environment name, or <c>null</c>.</param>
    internal static string ResolveDeploymentEnvironment(string? environmentName) =>
        ParseDeploymentEnvironment(Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"))
        ?? environmentName
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? "Production";

    /// <summary>
    /// Parses the <c>deployment.environment</c> key-value pair from an
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c> string (format: <c>key=value,key=value</c>).
    /// Returns <c>null</c> if the key is absent or the string is empty.
    /// </summary>
    internal static string? ParseDeploymentEnvironment(string? otelResourceAttributes)
    {
        if (string.IsNullOrWhiteSpace(otelResourceAttributes))
            return null;

        foreach (var pair in otelResourceAttributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = pair[..separatorIndex].Trim();
            if (string.Equals(key, "deployment.environment", StringComparison.OrdinalIgnoreCase))
                return pair[(separatorIndex + 1)..].Trim();
        }

        return null;
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
