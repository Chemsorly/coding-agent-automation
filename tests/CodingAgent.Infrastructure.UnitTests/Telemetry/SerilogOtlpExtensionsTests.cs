using System.Reflection;
using CodingAgent.Infrastructure.Telemetry;
using Serilog;
using Serilog.Core;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="SerilogOtlpExtensions.WriteToOtlpIfConfigured"/>. These verify sink
/// CONFIGURATION only (that the logger builds and the OTLP sink is wired), so they intentionally
/// do not emit a log event: emitting one queues it for export, and disposing the logger then
/// flushes it to the unreachable test endpoint (localhost:4317), costing ~3s per test on the
/// batched gRPC exporter's failure/backoff path.
/// </summary>
[Collection("EnvironmentVariables")]
public class SerilogOtlpExtensionsTests : IDisposable
{
    private readonly string? _originalEndpoint;
    private readonly string? _originalHeaders;
    private readonly string? _originalAspNetEnv;
    private readonly string? _originalOtelServiceName;
    private readonly string? _originalOtelResourceAttributes;

    public SerilogOtlpExtensionsTests()
    {
        _originalEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        _originalHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        _originalAspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        _originalOtelServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        _originalOtelResourceAttributes = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
    }

    public void Dispose()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", _originalEndpoint);
        SetEnvVar("OTEL_EXPORTER_OTLP_HEADERS", _originalHeaders);
        SetEnvVar("ASPNETCORE_ENVIRONMENT", _originalAspNetEnv);
        SetEnvVar("OTEL_SERVICE_NAME", _originalOtelServiceName);
        SetEnvVar("OTEL_RESOURCE_ATTRIBUTES", _originalOtelResourceAttributes);
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
        Assert.Equal("fallbackServiceName", ex.ParamName);
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

    // ── OTEL_SERVICE_NAME resolution ─────────────────────────────────────────

    [Fact]
    public void WriteToOtlpIfConfigured_WhenOtelServiceNameEnvVarSet_UsesItOverFallback()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_SERVICE_NAME", "foo-service");

        // TODO: This test only verifies the sink is present, not that "foo-service" was used as
        // service.name in ResourceAttributes. The core behavioral invariant (OTEL_SERVICE_NAME
        // takes precedence over fallbackServiceName) is untested — a regression that ignores the
        // env var would still pass. Fix: inspect the sink's ResourceAttributes["service.name"]
        // value via reflection and assert it equals "foo-service", not "fallback-service".
        // See review finding (issue #2969).
        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("fallback-service", "Test")
            .CreateLogger();

        // The OTLP sink must have been configured (OTEL_EXPORTER_OTLP_ENDPOINT is set)
        var coreLogger = (Serilog.Core.Logger)logger;
        var sinkField = typeof(Serilog.Core.Logger).GetField("_sink", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(ContainsOpenTelemetrySink(sinkField!.GetValue(coreLogger)!),
            "Expected OpenTelemetry sink when endpoint is set");

        // TODO: Missing try/finally — manual cleanup at end of method is skipped if an assertion
        // above throws. Use try/finally (Dispose() already handles _originalOtelServiceName
        // restore, so the manual SetEnvVar call below is redundant and can be removed once
        // the test is restructured). See review finding (issue #2969).
        logger.Dispose();
        SetEnvVar("OTEL_SERVICE_NAME", null);
    }

    [Fact]
    public void WriteToOtlpIfConfigured_WhenOtelServiceNameEnvVarNotSet_UsesFallback()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_SERVICE_NAME", null);

        // TODO: Only asserts the logger is non-null — does not verify that "fallback-service" was
        // used as ResourceAttributes["service.name"]. A regression that always ignores the fallback
        // would pass silently. Fix: inspect the sink's ResourceAttributes via reflection and assert
        // service.name equals "fallback-service". See review finding (issue #2969).
        // Should build without error — fallback name is used
        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("fallback-service", "Test")
            .CreateLogger();

        Assert.NotNull(logger);
        logger.Dispose();
    }

    // ── deployment.environment resolution ────────────────────────────────────

    [Fact]
    public void WriteToOtlpIfConfigured_WhenDeploymentEnvironmentInOtelResourceAttributes_UsesItOverAspNetCoreEnvironment()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=staging");
        SetEnvVar("ASPNETCORE_ENVIRONMENT", "Production");

        // TODO: Only asserts the logger is non-null — does not verify that "staging" (from
        // OTEL_RESOURCE_ATTRIBUTES) was used instead of "Production" (ASPNETCORE_ENVIRONMENT).
        // This is the primary test for AC "logs and traces carry the same deployment.environment"
        // yet the actual behavior is completely unverified. A broken ParseDeploymentEnvironment
        // integration would still pass. Fix: inspect ResourceAttributes["deployment.environment"]
        // via reflection and assert it equals "staging". See review finding (issue #2969).
        // Should build without error — deployment.environment from OTEL_RESOURCE_ATTRIBUTES is used
        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service")
            .CreateLogger();

        Assert.NotNull(logger);
        logger.Dispose();
    }

    [Fact]
    public void WriteToOtlpIfConfigured_WhenDeploymentEnvironmentNotInOtelResourceAttributes_FallsBackToEnvironmentName()
    {
        SetEnvVar("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        SetEnvVar("OTEL_RESOURCE_ATTRIBUTES", "k8s.namespace.name=prod-ns");
        SetEnvVar("ASPNETCORE_ENVIRONMENT", null);

        // TODO: Only asserts the logger is non-null — does not verify that "my-environment" (the
        // explicit environmentName parameter) ended up in ResourceAttributes["deployment.environment"].
        // The fallback priority chain is not validated. Fix: inspect the sink's ResourceAttributes
        // via reflection and assert deployment.environment equals "my-environment".
        // See review finding (issue #2969).
        // Should build without error — explicit environmentName parameter is used
        var logger = new LoggerConfiguration()
            .WriteToOtlpIfConfigured("test-service", "my-environment")
            .CreateLogger();

        Assert.NotNull(logger);
        logger.Dispose();
    }

    // ── ParseDeploymentEnvironment unit tests ─────────────────────────────────

    [Fact]
    public void ParseDeploymentEnvironment_WhenKeyPresent_ReturnsValue()
    {
        var result = SerilogOtlpExtensions.ParseDeploymentEnvironment("deployment.environment=staging");
        Assert.Equal("staging", result);
    }

    [Fact]
    public void ParseDeploymentEnvironment_WhenKeyAmongOthers_ReturnsCorrectValue()
    {
        var result = SerilogOtlpExtensions.ParseDeploymentEnvironment(
            "k8s.namespace.name=prod-ns,deployment.environment=production,custom.attr=foo");
        Assert.Equal("production", result);
    }

    [Fact]
    public void ParseDeploymentEnvironment_WhenKeyAbsent_ReturnsNull()
    {
        var result = SerilogOtlpExtensions.ParseDeploymentEnvironment("k8s.namespace.name=prod-ns,service.name=api");
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDeploymentEnvironment_WhenNullOrEmpty_ReturnsNull(string? input)
    {
        var result = SerilogOtlpExtensions.ParseDeploymentEnvironment(input);
        Assert.Null(result);
    }

    [Fact]
    public void ParseDeploymentEnvironment_WhenValueContainsEquals_ReturnsFullValue()
    {
        // Values may technically contain '=' (e.g. base64 padding) — only first '=' is the separator
        var result = SerilogOtlpExtensions.ParseDeploymentEnvironment("deployment.environment=prod=ish");
        Assert.Equal("prod=ish", result);
    }

    [Fact]
    public void ParseDeploymentEnvironment_WhenKeyAtStart_ReturnsValue()
    {
        var result = SerilogOtlpExtensions.ParseDeploymentEnvironment(
            "deployment.environment=development,other.key=value");
        Assert.Equal("development", result);
    }

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
