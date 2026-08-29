using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests that verify <see cref="PipelineSignalRReporter.EmitOutputLine"/> produces exactly
/// one Serilog <see cref="LogEventLevel.Debug"/> entry per call, with <c>StepName</c>
/// correctly captured from <see cref="LogContext"/> when called inside a step body.
/// </summary>
/// <remarks>
/// Uses a real Serilog <see cref="ILogger"/> backed by <see cref="CaptureSink"/> — not
/// <see cref="Moq.Mock{T}"/> — because only a real logger participates in Serilog's
/// enrichment pipeline and captures <see cref="LogContext"/> properties.
/// </remarks>
public class PipelineSignalRReporterLoggingTests
{
    private readonly CaptureSink _sink;
    private readonly ILogger _captureLogger;

    public PipelineSignalRReporterLoggingTests()
    {
        _sink = new CaptureSink();
        _captureLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Debug()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    // ── EmitOutputLine Serilog bridge ──────────────────────────────────

    [Fact]
    public async Task EmitOutputLine_CallsLoggerOnce_WithMessageText()
    {
        // Arrange
        await using var reporter = CreateReporter(_captureLogger);

        // Act
        reporter.EmitOutputLine("test line", context: null, ct: CancellationToken.None);

        // Assert — exactly one log event at Debug level
        _sink.Events.Should().HaveCount(1);
        var logEvent = _sink.Events.Single();
        logEvent.Level.Should().Be(LogEventLevel.Debug);
        // The rendered message should contain the line text via the {Line} property
        logEvent.Properties.Should().ContainKey("Line");
        logEvent.Properties["Line"].ToString().Should().Contain("test line");
    }

    [Fact]
    public async Task EmitOutputLine_WithStepNameInLogContext_LogsWithStepName()
    {
        // Arrange
        await using var reporter = CreateReporter(_captureLogger);

        // Act — push context properties and call EmitOutputLine while the scope is still open.
        // (simulates PipelineStepRunner.ExecuteAsync which pushes StepName before calling step.ExecuteAsync)
        // The synchronous _logger.Debug call in EmitOutputLine must capture the LogContext properties
        // BEFORE the fire-and-forget continuation escapes the AsyncLocal scope.
        IDisposable? stepCtx = Serilog.Context.LogContext.PushProperty("StepName", "CloneRepository");
        IDisposable? runCtx = Serilog.Context.LogContext.PushProperty("PipelineRunId", "run-test-1");

        reporter.EmitOutputLine("🔄 Cloning repository...", context: null, ct: CancellationToken.None);

        // Dispose LogContext scopes *before* asserting to prove StepName was captured synchronously
        // at the _logger.Debug call site. If _logger.Debug were inside EmitOutputLineInternalAsync
        // (the async continuation), the event would be logged *after* the scope disposes and
        // StepName would be absent from the captured event — this assertion would then fail.
        runCtx.Dispose();
        stepCtx.Dispose();

        // Assert — captured event has StepName and PipelineRunId even though the scopes are now disposed,
        // proving the values were captured synchronously before the async continuation was discarded.
        _sink.Events.Should().HaveCount(1);
        var logEvent = _sink.Events.Single();
        logEvent.Properties.Should().ContainKey("StepName");
        logEvent.Properties["StepName"].ToString().Should().Contain("CloneRepository");
        logEvent.Properties.Should().ContainKey("PipelineRunId");
        logEvent.Properties["PipelineRunId"].ToString().Should().Contain("run-test-1");
        // TODO: [WARNING] Acceptance criteria also requires IssueIdentifier and AgentId to be present in the
        // structured log entry. This test only verifies StepName and PipelineRunId — a regression where
        // either IssueIdentifier or AgentId is missing from LogContext would not be caught. Push both
        // properties in the LogContext above and add matching assertions here.
    }

    [Fact]
    public async Task EmitOutputLine_OutsideStepContext_LogsWithoutStepName()
    {
        // Arrange — no LogContext push (simulates lifecycle-boundary calls like cancellation)
        await using var reporter = CreateReporter(_captureLogger);

        // Act — no LogContext.PushProperty("StepName") — simulates cancellation path in LocalPipelineExecutor
        reporter.EmitOutputLine("🚫 Pipeline cancelled", context: null, ct: CancellationToken.None);

        // Assert — logger fires once, StepName is absent (lifecycle-boundary behavior is acceptable per AC)
        _sink.Events.Should().HaveCount(1);
        var logEvent = _sink.Events.Single();
        logEvent.Properties.Should().NotContainKey("StepName");
        logEvent.Level.Should().Be(LogEventLevel.Debug);
        // TODO: [WARNING] This test does not verify the synchronous-capture property: that StepName is
        // absent *after* a LogContext using-scope disposes. Add a second call to EmitOutputLine outside
        // a LogContext.PushProperty("StepName") scope (after an inner using block is disposed) and assert
        // StepName is absent, confirming that StepName is captured synchronously and does not leak across calls.
    }

    [Fact]
    public async Task EmitOutputLine_MultipleCallsInSameScope_ProducesOneEntryPerCall()
    {
        // Arrange
        await using var reporter = CreateReporter(_captureLogger);

        // Act
        reporter.EmitOutputLine("line one", context: null, ct: CancellationToken.None);
        reporter.EmitOutputLine("line two", context: null, ct: CancellationToken.None);
        reporter.EmitOutputLine("line three", context: null, ct: CancellationToken.None);

        // Assert — exactly 3 entries, one per call (no batching or deduplication)
        _sink.Events.Should().HaveCount(3);
    }

    [Fact]
    public async Task EmitOutputLine_SecretInOutput_MasksBeforeLogging()
    {
        // Arrange — create a context with a real injected secret so MaskSecretsInOutput actually masks.
        // The logged {Line} value must contain "***" and must NOT contain the raw secret, proving
        // that _logger.Debug is called with the masked value rather than the raw input.
        // If _logger.Debug(masked) were accidentally rewritten as _logger.Debug(line), this test fails
        // and the raw secret would be visible in Loki.
        const string rawSecret = "super-secret-token-xyz";
        const string rawLine = $"Connecting with token={rawSecret}";

        var context = CreateContextWithSecrets(new Dictionary<string, string>
        {
            ["API_TOKEN"] = rawSecret
        });

        await using var reporter = CreateReporter(_captureLogger);

        // Act
        reporter.EmitOutputLine(rawLine, context: context, ct: CancellationToken.None);

        // Assert — the logged {Line} property must contain the masked value, not the raw secret
        _sink.Events.Should().HaveCount(1);
        var loggedLine = _sink.Events.Single().Properties["Line"].ToString();
        loggedLine.Should().Contain("***");
        loggedLine.Should().NotContain(rawSecret,
            "the raw secret must never reach the Serilog sink; _logger.Debug must be called with the masked value");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static PipelineSignalRReporter CreateReporter(ILogger logger)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
        var run = new PipelineRun
        {
            RunId = "run-sig-test",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        var batcher = new OutputBatcher();
        return new PipelineSignalRReporter(connection, batcher, "job-sig-test", run, null, logger);
    }

    /// <summary>
    /// Creates a minimal <see cref="PipelineStepContext"/> with the given <paramref name="secrets"/>
    /// populated on <see cref="PipelineStepContext.InjectedSecrets"/>.
    /// Only used for tests that exercise the secret-masking path in <see cref="PipelineSignalRReporter.EmitOutputLine"/>.
    /// </summary>
    private static PipelineStepContext CreateContextWithSecrets(Dictionary<string, string> secrets)
    {
        var run = new PipelineRun
        {
            RunId = "run-secret-test",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        var logger = new Mock<Serilog.ILogger>().Object;
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = new Mock<IRepositoryProvider>().Object,
            AgentProvider = new Mock<IAgentProvider>().Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = new Mock<IConfigurationStore>().Object,
            Callbacks = new Mock<IPipelineCallbacks>().Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            AgentExecution = new Mock<IAgentPhaseExecutor>().Object,
            QualityGates = new Mock<IQualityGateExecutor>().Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(logger),
            Logger = logger,
            InjectedSecrets = secrets
        };
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

/// <summary>
/// A simple in-memory Serilog sink that captures all log events for test assertions.
/// Thread-safe via <see cref="ConcurrentQueue{T}"/>.
/// </summary>
internal sealed class CaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
