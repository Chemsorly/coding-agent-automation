using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using Serilog;
using Serilog.Events;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// Verifies that JobSpecBuilder logs at Error level before throwing when
/// JsonElement deserialization returns null.
/// </summary>
[Collection("SerilogLoggerTests")]
public class JobSpecBuilderLoggingTests
{
    /// <summary>
    /// Captures log events written to Serilog's global Log.Logger during test execution.
    /// JobSpecBuilder is a static class so it uses Log.Logger. Tests running in parallel that log
    /// through the static Log also write into this capture, from their own threads.
    /// </summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly ILogger _previousLogger;
        private readonly List<LogEvent> _events = [];

        /// <summary>Returns a thread-safe snapshot of captured events.</summary>
        public List<LogEvent> Events
        {
            get { lock (_events) return [.._events]; }
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
    public void Build_NullPodSecurityContextDeserialization_LogsErrorBeforeThrowing()
    {
        using var capture = new LogCapture();

        // A "null" JsonElement for podSecurityContext will deserialize to null
        // triggering the throw in DeserializeK8s
        var template = new JobTemplate
        {
            Labels = "test",
            Image = "test-image",
            ProviderType = "kiro",
            PodSecurityContext = JsonDocument.Parse("null").RootElement
        };

        var ctx = new JobSpecBuilder.BuildContext
        {
            WorkItemId = Guid.NewGuid(),
            AgentSelector = "test",
            TimeoutSeconds = 60,
            JobName = "test-job",
            ClaimedPvc = null,
            OrchestratorUrl = "http://localhost",
            AgentApiKeySecretName = "secret",
            AgentServiceAccountName = "sa",
            Namespace = "default"
        };

        var act = () => JobSpecBuilder.Build(template, ctx);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deserialize*");

        capture.Events.Should().Contain(e => e.Level == LogEventLevel.Error
            && e.MessageTemplate.Text.StartsWith("Failed to deserialize JsonElement", StringComparison.Ordinal));
    }
}
