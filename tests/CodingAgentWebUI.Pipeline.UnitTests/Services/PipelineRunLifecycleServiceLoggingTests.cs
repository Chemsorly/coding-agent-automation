using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests that verify <see cref="PipelineRunLifecycleService.EmitOutputLine"/> produces exactly
/// one Serilog <see cref="LogEventLevel.Information"/> entry per call, with the <c>RunId</c>
/// context property present when an <see cref="PipelineRunLifecycleService.ActiveRun"/> is set.
/// These tests use a real Serilog <see cref="ILogger"/> backed by <see cref="LifecycleCaptureSink"/>
/// rather than Moq, so enrichment and ForContext behaviour is exercised accurately.
/// </summary>
public class PipelineRunLifecycleServiceLoggingTests
{
    private readonly LifecycleCaptureSink _sink;
    private readonly ILogger _captureLogger;

    public PipelineRunLifecycleServiceLoggingTests()
    {
        _sink = new LifecycleCaptureSink();
        _captureLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Debug()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    // ── EmitOutputLine Serilog bridge ──────────────────────────────────

    [Fact]
    public void EmitOutputLine_ProducesInformationEntry_WithRunIdContext()
    {
        // Arrange
        var service = CreateService();
        var run = CreateRun("run-log-test-1");
        service.ActiveRun = run;

        // Act
        service.EmitOutputLine("❌ Pipeline failed: timeout");

        // Assert — exactly one Information entry with PipelineRunId as a LogContext enrichment property
        _sink.Events.Should().HaveCount(1);
        var logEvent = _sink.Events.Single();
        logEvent.Level.Should().Be(LogEventLevel.Information);
        // PipelineRunId is pushed via LogContext.PushProperty — it appears as a top-level structured
        // enrichment property, consistent with LocalPipelineExecutor and PipelineOrchestrationService,
        // and queryable in Grafana as {PipelineRunId="..."} without parsing the message body.
        logEvent.Properties.Should().ContainKey("PipelineRunId");
        // TODO: [WARNING] This uses .Contain() which is a substring match — it would pass if the property
        // contained "run-log-test-1-extra" or any other value containing the substring. Use an exact-match
        // assertion (e.g. unwrap the ScalarValue and assert the typed value) to catch regressions where a
        // wrong-but-containing run ID is emitted. (#2178)
        logEvent.Properties["PipelineRunId"].ToString().Should().Contain("run-log-test-1");
        logEvent.Properties.Should().ContainKey("Line");
        logEvent.Properties["Line"].ToString().Should().Contain("Pipeline failed: timeout");
    }

    [Fact]
    public void EmitOutputLine_SingleEmission_NoDoubleLog()
    {
        // Arrange — single call must produce exactly one Serilog entry (single-emission guarantee)
        var service = CreateService();
        service.ActiveRun = CreateRun("run-log-test-2");

        // Act
        service.EmitOutputLine("🚫 Pipeline cancelled");

        // Assert — exactly one log entry, not two
        // TODO: [WARNING] This test name "NoDoubleLog" overpromises: it only verifies that a single call to
        // EmitOutputLine produces at most one Serilog entry (a trivially true property for a non-recursive
        // synchronous method). It does NOT detect the cross-class double-emission scenario described in #2178,
        // where both PipelineSignalRReporter.EmitOutputLine and PipelineRunLifecycleService.EmitOutputLine
        // fire for the same logical event. A true double-emission guard would require an integration-level
        // test at the LocalPipelineExecutor call site. (#2178)
        _sink.Events.Should().HaveCount(1);
    }

    [Fact]
    public void EmitOutputLine_UIEventAlsoFires()
    {
        // Arrange — regression guard: OnOutputLine must still fire after Serilog call is added
        var service = CreateService();
        service.ActiveRun = CreateRun("run-log-test-3");

        var received = new List<string>();
        service.OnOutputLine += msg => received.Add(msg);

        // Act
        service.EmitOutputLine("🔍 Starting analysis gate...");

        // Assert — both the Serilog entry and the UI event fired with the same message
        _sink.Events.Should().HaveCount(1);
        received.Should().HaveCount(1);
        received.Single().Should().Be("🔍 Starting analysis gate...");
        // TODO: [WARNING] This test only covers the PipelineRunLifecycleService.OnOutputLine path. There is
        // no equivalent regression guard in PipelineSignalRReporterLoggingTests verifying that
        // EmitOutputLineInternalAsync still dispatches to SignalR after the Serilog call was added.
        // A regression dropping the `_ = EmitOutputLineInternalAsync(...)` call in PipelineSignalRReporter
        // would go undetected in the reporter-layer tests. (#2178)
    }

    [Fact]
    public void EmitOutputLine_WhenNoActiveRun_StillLogsWithNullRunId()
    {
        // Arrange — no ActiveRun set (e.g. called before pipeline starts or after completion)
        var service = CreateService();
        service.ActiveRun = null;

        // Act — must not throw; null RunId is acceptable
        var act = () => service.EmitOutputLine("some message with no active run");

        // Assert — no exception thrown; one Information entry is still produced
        act.Should().NotThrow();
        _sink.Events.Should().HaveCount(1);
        var logEvent = _sink.Events.Single();
        logEvent.Level.Should().Be(LogEventLevel.Information);
        // PipelineRunId is present as a LogContext enrichment property; its value is null when ActiveRun is null.
        logEvent.Properties.Should().ContainKey("PipelineRunId");
        // TODO: [WARNING] This assertion does not verify that the PipelineRunId value is null. When ActiveRun
        // is null, runId will be null and Serilog captures it as ScalarValue(null). Without asserting the value,
        // a regression where a stale or default run ID is serialized instead of null would not be caught.
        // Add: logEvent.Properties["PipelineRunId"].Should().Match(v => v is ScalarValue sv && sv.Value == null) (#2178)
    }

    [Fact]
    public void EmitOutputLine_MultipleCallsProduceOneEntryEach()
    {
        // Arrange
        var service = CreateService();
        service.ActiveRun = CreateRun("run-log-test-multi");

        // Act
        service.EmitOutputLine("line one");
        service.EmitOutputLine("line two");
        service.EmitOutputLine("line three");

        // Assert — exactly 3 entries, one per call
        _sink.Events.Should().HaveCount(3);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private PipelineRunLifecycleService CreateService()
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        var mockRunService = new Mock<IOrchestratorRunService>();
        return new PipelineRunLifecycleService(
            mockHistory.Object,
            mockRunService.Object,
            _captureLogger);
    }

    private static PipelineRun CreateRun(string runId = "run-1")
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = PipelineStep.Created,
            HighWaterMark = PipelineStep.Created,
            StartedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// A simple in-memory Serilog sink that captures all log events for test assertions.
/// Thread-safe via <see cref="ConcurrentQueue{T}"/>. Defined locally to avoid a dependency
/// on the Agent test project's <c>CaptureSink</c>.
/// </summary>
internal sealed class LifecycleCaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
