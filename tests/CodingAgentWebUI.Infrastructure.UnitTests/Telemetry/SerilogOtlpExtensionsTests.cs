using System.Reflection;
using CodingAgentWebUI.Infrastructure.Telemetry;
using Serilog;
using Serilog.Core;

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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

        Assert.NotNull(logger);
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

            // Assert the OTLP sink was configured — endpoint is set so WriteTo.OpenTelemetry() must have been called.
            // Walk the sink tree to verify that at least one sink in the chain is an OpenTelemetry sink.
            var coreLogger = (Serilog.Core.Logger)logger;
            var sinkField = typeof(Serilog.Core.Logger)
                .GetField("_sink", BindingFlags.NonPublic | BindingFlags.Instance);
            var rootSink = sinkField!.GetValue(coreLogger)!;
            Assert.True(ContainsOpenTelemetrySink(rootSink), "Expected an OpenTelemetry sink to be configured when OTEL_EXPORTER_OTLP_ENDPOINT is set.");

            logger.Dispose();
        }
        finally
        {
            SetEnvVar("OTEL_EXPORTER_OTLP_PROTOCOL", null);
        }
    }

    private static void SetEnvVar(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value);

    /// <summary>
    /// Recursively searches the sink tree (via known aggregate and wrapper fields) for an OpenTelemetry sink.
    /// </summary>
    private static bool ContainsOpenTelemetrySink(object sink, int depth = 0)
    {
        if (depth > 10) return false; // guard against cycles

        var typeName = sink.GetType().FullName ?? "";
        if (typeName.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check child sinks in aggregate-style fields named "_sinks" or "_sink"
        foreach (var fieldName in new[] { "_sinks", "_sink", "_wrapped" })
        {
            var field = sink.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) continue;

            var value = field.GetValue(sink);
            if (value is IEnumerable<ILogEventSink> many)
            {
                foreach (var child in many)
                    if (ContainsOpenTelemetrySink(child, depth + 1)) return true;
            }
            else if (value is ILogEventSink one)
            {
                if (ContainsOpenTelemetrySink(one, depth + 1)) return true;
            }
            else if (value is object[] objArr)
            {
                foreach (var item in objArr.OfType<ILogEventSink>())
                    if (ContainsOpenTelemetrySink(item, depth + 1)) return true;
            }
        }

        return false;
    }
}
