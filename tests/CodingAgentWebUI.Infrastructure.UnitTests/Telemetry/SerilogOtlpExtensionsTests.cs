using CodingAgentWebUI.Infrastructure.Telemetry;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Telemetry;

// TODO: Add [Collection("EnvironmentVariables")] to prevent parallel test interference from env var mutations
public class SerilogOtlpExtensionsTests : IDisposable
{
    private readonly string? _originalEndpoint;
    private readonly string? _originalHeaders;
    private readonly string? _originalAspNetEnv;

    public SerilogOtlpExtensionsTests()
    {
        _originalEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        _originalHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        _originalAspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    }

    public void Dispose()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", _originalEndpoint);
        SetEnvVar("OTEL_EXPORTER_OTLP_HEADERS", _originalHeaders);
        SetEnvVar("ASPNETCORE_ENVIRONMENT", _originalAspNetEnv);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WriteToOtlpIfConfigured_WhenEndpointIsNullOrEmpty_LoggerBuildsWithoutError(string? endpoint)
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);

        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    [Fact]
    public void WriteToOtlpIfConfigured_WhenEndpointIsSet_LoggerBuildsWithoutError()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");

        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service", "Development")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    [Fact]
    public void WriteToOtlpIfConfigured_FallsBackToAspNetCoreEnvironment()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("ASPNETCORE_ENVIRONMENT", "Staging");

        // Should not throw — environmentName falls back to env var
        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    [Theory]
    [InlineData("key=value")]
    [InlineData("Authorization=Bearer token=abc")]
    [InlineData("key1=value1,key2=value2")]
    public void WriteToOtlpIfConfigured_ParsesHeadersWithoutError(string headers)
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_EXPORTER_OTLP_HEADERS", headers);

        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service", "Test")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    [Fact]
    public void WriteToOtlpIfConfigured_HandlesEmptyHeaders()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_EXPORTER_OTLP_HEADERS", "");

        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service", "Test")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    private static void SetEnvVar(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value);
}
