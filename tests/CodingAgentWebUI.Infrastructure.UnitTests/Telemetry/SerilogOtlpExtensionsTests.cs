using CodingAgentWebUI.Infrastructure.Telemetry;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Telemetry;

[Collection("EnvironmentVariables")]
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

    [Theory]
    [InlineData("key1=value%3Dencoded")]
    [InlineData("key2=value%20with%20spaces")]
    [InlineData("x-custom%2Dheader=val1,x-other=val2")]
    public void WriteToOtlpIfConfigured_UrlEncodedHeaders_BuildsWithoutError(string headers)
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_EXPORTER_OTLP_HEADERS", headers);

        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service", "Test")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    [Theory]
    [InlineData("no-equals-sign")]
    [InlineData("=value-without-key")]
    [InlineData("%20=value")]
    public void WriteToOtlpIfConfigured_InvalidHeaders_BuildsWithoutError(string headers)
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
    public void WriteToOtlpIfConfigured_MixedValidAndInvalidHeaders_BuildsWithoutError()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_EXPORTER_OTLP_HEADERS", "valid=ok,invalid,also=good");

        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service", "Test")
            .CreateLogger();

        logger.Information("Test message");
        logger.Dispose();
    }

    [Fact]
    public void WriteToOtlpIfConfigured_NullLoggerConfiguration_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            SerilogOtlpExtensions.WriteToOtlpIfConfigured(null!, "svc"));
        Assert.Equal("loggerConfiguration", ex.ParamName);
    }

    [Fact]
    public void WriteToOtlpIfConfigured_NullServiceName_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new LoggerConfiguration().WriteToOtlpIfConfigured(null!));
        Assert.Equal("serviceName", ex.ParamName);
    }

    [Theory]
    [InlineData("http/protobuf", "http://localhost:4318")]
    [InlineData("grpc", "http://localhost:4317")]
    [InlineData(null, "http://localhost:4317")]
    public void WriteToOtlpIfConfigured_RespectsProtocolEnvVar(string? protocol, string endpoint)
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);
        SetEnvVar("OTEL_EXPORTER_OTLP_PROTOCOL", protocol);
        try
        {
            // Should build without error regardless of protocol
            var logger = new LoggerConfiguration()
                .WriteToOtlpIfConfigured("test-service", "Test")
                .CreateLogger();

            logger.Information("Test message");
            logger.Dispose();
        }
        finally
        {
            SetEnvVar("OTEL_EXPORTER_OTLP_PROTOCOL", null);
        }
    }

    private static void SetEnvVar(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value);
}
