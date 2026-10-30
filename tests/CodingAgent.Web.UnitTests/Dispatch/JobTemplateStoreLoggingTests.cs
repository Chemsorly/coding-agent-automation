using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// Verifies that JobTemplateStore logs at Error level before throwing exceptions
/// for null deserialization results, missing files, and invalid template configs.
/// </summary>
[Collection("SerilogLoggerTests")]
public class JobTemplateStoreLoggingTests
{
    /// <summary>
    /// Captures log events written to Serilog's global Log.Logger during test execution.
    /// JobTemplateStore is a non-DI static-factory class so it uses Log.Logger.
    /// </summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly ILogger _previousLogger;
        private readonly List<LogEvent> _events = [];

        /// <summary>Returns a thread-safe snapshot of captured events.</summary>
        public List<LogEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public LogCapture()
        {
            _previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new ListSink(_events))
                .CreateLogger();
        }

        public void Dispose()
        {
            Log.Logger = _previousLogger;
        }

        private sealed class ListSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
        {
            public void Emit(LogEvent logEvent)
            {
                lock (events)
                    events.Add(logEvent);
            }
        }
    }

    [Fact]
    public void LoadFromYaml_NullResult_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        // Empty YAML produces null deserialization
        var act = () => JobTemplateStore.LoadFromYaml("");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*null*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void LoadFromJson_NullResult_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        // "null" JSON string produces null deserialization
        var act = () => JobTemplateStore.LoadFromJson("null");

        act.Should().Throw<System.Text.Json.JsonException>()
            .WithMessage("*null*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void LoadFromFile_MissingFile_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        var act = () => JobTemplateStore.LoadFromFile("/nonexistent/path/job-templates.yaml");

        act.Should().Throw<FileNotFoundException>();

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void BuildLookup_EmptyImage_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        // Template with empty image should fail during BuildLookup
        const string json = """
        [{ "labels": "kiro,dotnet", "image": "", "providerType": "kiro" }]
        """;

        var act = () => JobTemplateStore.LoadFromJson(json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty Image*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    // ── LoadFromConfiguration ────────────────────────────────────────────────

    [Theory]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    public void LoadFromConfiguration_MissingFile_ReturnsEmptyStoreAndLogsAtRequestedLevel(LogEventLevel level)
    {
        using var capture = new LogCapture();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                { { "WorkDistribution:JobTemplatesPath", "/nonexistent/path/job-templates.yaml" } })
            .Build();
        const string msg = "Job templates file not found at {Path}; test message";

        var store = JobTemplateStore.LoadFromConfiguration(config, level, msg);

        store.GetAllTemplates().Should().BeEmpty();
        // Filter by level AND the specific path value so that Warning events emitted by other
        // parallel test classes that also write to the global Log.Logger (e.g. with a {Path}
        // property for a different path) cannot cause a false ContainSingle failure.
        // The path "/nonexistent/path/job-templates.yaml" is unique to this test call.
        capture.Events.Should().ContainSingle(
            e => e.Level == level
                 && e.Properties.ContainsKey("Path")
                 && e.Properties["Path"].ToString().Contains("nonexistent/path/job-templates.yaml"),
            $"exactly one {level} event with the expected path must be emitted");
        var ev = capture.Events.Single(
            e => e.Level == level
                 && e.Properties.ContainsKey("Path")
                 && e.Properties["Path"].ToString().Contains("nonexistent/path/job-templates.yaml"));
        // Verify the exact Serilog-rendered value; a Serilog ScalarValue wraps strings in double
        // quotes (e.g. "\"/nonexistent/path/…\""). This goes beyond the Contains predicate above
        // and would catch a value-type or formatting regression that the filter cannot catch.
        ev.Properties["Path"].ToString().Should().Be("\"/nonexistent/path/job-templates.yaml\"");
    }

    [Fact]
    public void LoadFromConfiguration_FileExists_LoadsTemplates()
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".yaml");
        try
        {
            File.WriteAllText(path, "- labels: dotnet,kiro\n  image: test:latest\n  providerType: kiro\n");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                    { { "WorkDistribution:JobTemplatesPath", path } })
                .Build();

            var store = JobTemplateStore.LoadFromConfiguration(config, LogEventLevel.Warning, "unused {Path}");

            store.GetAllTemplates().Should().HaveCount(1);
            store.Resolve("dotnet,kiro").Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromConfiguration_AbsentConfigKey_UsesDefaultPath()
    {
        using var capture = new LogCapture();
        // Build config with no WorkDistribution:JobTemplatesPath key — loader must fall back to default
        var config = new ConfigurationBuilder().Build();
        const string msg = "Job templates file not found at {Path}";

        // The default path /app/config/job-templates.yaml does not exist in the test environment,
        // so the loader returns an empty store and logs the missing-file message with the default path.
        var store = JobTemplateStore.LoadFromConfiguration(config, LogEventLevel.Warning, msg);

        store.GetAllTemplates().Should().BeEmpty();
        // Filter by both level and the Path property so that unrelated Warning events emitted by
        // concurrently-running test classes (which share the global Log.Logger) don't cause a
        // false ContainSingle failure.
        capture.Events.Should().ContainSingle(e => e.Level == LogEventLevel.Warning && e.Properties.ContainsKey("Path"));
        var ev = capture.Events.Single(e => e.Level == LogEventLevel.Warning && e.Properties.ContainsKey("Path"));
        ev.Properties["Path"].ToString().Should().Contain("job-templates.yaml");
    }
}
