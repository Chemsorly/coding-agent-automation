using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using Serilog;
using Serilog.Events;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Verifies that JobTemplateProvider logs at Error level before throwing exceptions
/// for null deserialization results, missing files, and invalid template configs.
/// </summary>
[Collection("SerilogLoggerTests")]
public class JobTemplateProviderLoggingTests
{
    /// <summary>
    /// Captures log events written to Serilog's global Log.Logger during test execution.
    /// JobTemplateProvider is a non-DI static-factory class so it uses Log.Logger.
    /// </summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly ILogger _previousLogger;
        public List<LogEvent> Events { get; } = [];

        public LogCapture()
        {
            _previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new ListSink(Events))
                .CreateLogger();
        }

        public void Dispose()
        {
            Log.Logger = _previousLogger;
        }

        private sealed class ListSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
        {
            public void Emit(LogEvent logEvent) => events.Add(logEvent);
        }
    }

    [Fact]
    public void LoadFromYaml_NullResult_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        // Empty YAML produces null deserialization
        var act = () => JobTemplateProvider.LoadFromYaml("");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*null*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void LoadFromJson_NullResult_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        // "null" JSON string produces null deserialization
        var act = () => JobTemplateProvider.LoadFromJson("null");

        act.Should().Throw<System.Text.Json.JsonException>()
            .WithMessage("*null*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void LoadFromFile_MissingFile_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        var act = () => JobTemplateProvider.LoadFromFile("/nonexistent/path/job-templates.yaml");

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

        var act = () => JobTemplateProvider.LoadFromJson(json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty Image*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }
}
