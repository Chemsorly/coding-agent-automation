using System.Reflection;
using CodingAgent.Infrastructure.Telemetry;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="HostBootstrap"/>.
///
/// Split into two classes to apply the correct xUnit collection per concern:
/// - <see cref="HostBootstrapCreateLoggerTests"/> — tests for <c>CreateBootstrapLogger()</c>,
///   which never touches the static <c>Log.Logger</c>. No collection required.
/// - <see cref="HostBootstrapStartupIdentityTests"/> — tests for <c>LogStartupIdentity()</c>,
///   which writes to the process-global <c>Log.Logger</c>. Must use
///   <c>[Collection("StaticLogger")]</c> to serialise against other tests that mutate
///   <c>Log.Logger</c> and prevent sink-capture races.
/// </summary>
public class HostBootstrapCreateLoggerTests
{
    [Fact]
    // TODO: This test only asserts the return value is non-null. CreateBootstrapLogger() can never
    // realistically return null, so this assertion passes regardless of any misconfiguration of the
    // sink or minimum level. Replace with a behavioural assertion (e.g. verify that an Information
    // event produces output to Console.Out while a Debug/Verbose event is suppressed) to constrain
    // the contract meaningfully. (review-findings #2865)
    public void CreateBootstrapLogger_ReturnsNonNullLogger()
    {
        var logger = HostBootstrap.CreateBootstrapLogger();

        Assert.NotNull(logger);
        (logger as IDisposable)?.Dispose();
    }

    [Fact]
    // TODO: This test verifies the console sink via deep private-field reflection, which is brittle:
    // it will silently pass (false-negative) if Serilog renames the sink type, restructures internal
    // fields, or the depth limit of 10 is exceeded before reaching the sink. Replace with an
    // observable-behaviour assertion — redirect Console.Out before calling CreateBootstrapLogger(),
    // write an Information event, and assert that output was produced — so the test survives Serilog
    // internal changes. (review-findings #2865)
    public void CreateBootstrapLogger_BuildsBootstrapLogger_WithConsoleSink()
    {
        var logger = HostBootstrap.CreateBootstrapLogger();

        // CreateBootstrapLogger() returns a Serilog.Extensions.Hosting.ReloadableLogger,
        // not a Serilog.Core.Logger. Walk its internal fields via reflection to confirm
        // a console sink is present somewhere in the sink tree.
        Assert.True(ContainsConsoleSinkAnywhere(logger),
            $"Expected a console sink to be configured in the bootstrap logger (type: {logger.GetType().FullName}).");

        (logger as IDisposable)?.Dispose();
    }

    [Fact]
    public void CreateBootstrapLogger_CalledTwice_DoesNotThrow()
    {
        var logger1 = HostBootstrap.CreateBootstrapLogger();
        var logger2 = HostBootstrap.CreateBootstrapLogger();

        Assert.NotNull(logger1);
        Assert.NotNull(logger2);

        (logger1 as IDisposable)?.Dispose();
        (logger2 as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Walks all private fields of an arbitrary Serilog logger/sink object looking for any
    /// field value whose type name contains "Console". Works on both <c>ReloadableLogger</c>
    /// (returned by <c>CreateBootstrapLogger()</c>) and <c>Serilog.Core.Logger</c>.
    /// </summary>
    private static bool ContainsConsoleSinkAnywhere(object obj, int depth = 0)
    {
        if (depth > 10 || obj is null) return false;

        var type = obj.GetType();
        var typeName = type.FullName ?? "";

        if (typeName.Contains("Console", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var value = field.GetValue(obj);
            if (value is null) continue;

            if (field.FieldType.IsValueType) continue; // skip primitives

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                    if (item is not null && ContainsConsoleSinkAnywhere(item, depth + 1))
                        return true;
            }
            else
            {
                if (ContainsConsoleSinkAnywhere(value, depth + 1)) return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Tests for <see cref="HostBootstrap.LogStartupIdentity"/> that mutate the process-global
/// <c>Log.Logger</c>. Must be in <c>[Collection("StaticLogger")]</c> so they run serially
/// with other <c>StaticLogger</c>-collection tests and avoid sink-capture races.
/// </summary>
[Collection("StaticLogger")]
public class HostBootstrapStartupIdentityTests : IDisposable
{
    private readonly ILogger _previousLogger;
    private readonly CollectingSink _sink;

    public HostBootstrapStartupIdentityTests()
    {
        _previousLogger = Log.Logger;
        _sink = new CollectingSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
    }

    [Fact]
    // TODO: This test covers only one input set. Empty strings are not guarded by
    // ArgumentNullException.ThrowIfNull, so LogStartupIdentity("", "", "") silently emits a
    // log event with empty property values — a realistic scenario when env vars are unset and
    // callers fall back to "" rather than null. Add [Theory]/[InlineData] cases for empty
    // serviceLabel, serviceName, and version to define and verify the expected behaviour
    // (e.g. assert ArgumentException is thrown, or assert the log event is emitted with the
    // empty string, whichever the team decides). (review-findings #2865)
    public void LogStartupIdentity_WritesExpectedMessageToLog()
    {
        HostBootstrap.LogStartupIdentity("Scheduler", "coding-agent-scheduler", "1.2.3");

        // Filter to only the startup-identity event — concurrent tests in the same assembly that
        // use the static Log.Logger (e.g. ResiliencePipelineFactoryTests retry scenarios) can
        // emit Warning events into this sink. We assert on exactly one startup event rather than
        // Assert.Single(all events) to avoid flakiness from that unrelated log noise.
        var startupEvents = _sink.Events
            .Where(e => e.MessageTemplate.Text.Contains("starting:"))
            .ToList();
        Assert.Single(startupEvents);
        var evt = startupEvents[0];
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.True(
            evt.Properties.TryGetValue("ServiceLabel", out var labelProp) &&
            labelProp.ToString().Trim('"') == "Scheduler",
            "Expected ServiceLabel property to be 'Scheduler'");
        Assert.True(
            evt.Properties.TryGetValue("ServiceName", out var nameProp) &&
            nameProp.ToString().Trim('"') == "coding-agent-scheduler",
            "Expected ServiceName property to be 'coding-agent-scheduler'");
        Assert.True(
            evt.Properties.TryGetValue("Version", out var versionProp) &&
            versionProp.ToString().Trim('"') == "1.2.3",
            "Expected Version property to be '1.2.3'");
    }

    [Fact]
    public void LogStartupIdentity_NullLabel_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => HostBootstrap.LogStartupIdentity(null!, "coding-agent-api", "1.0.0"));
        Assert.Equal("serviceLabel", ex.ParamName);
    }

    [Fact]
    public void LogStartupIdentity_NullServiceName_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => HostBootstrap.LogStartupIdentity("API", null!, "1.0.0"));
        Assert.Equal("serviceName", ex.ParamName);
    }

    [Fact]
    public void LogStartupIdentity_NullVersion_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => HostBootstrap.LogStartupIdentity("API", "coding-agent-api", null!));
        Assert.Equal("version", ex.ParamName);
    }

    /// <summary>
    /// Thread-safe sink that collects log events for assertion.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_lock) { return _events.ToList(); } }
        }

        public void Emit(LogEvent logEvent) { lock (_lock) { _events.Add(logEvent); } }
    }
}
