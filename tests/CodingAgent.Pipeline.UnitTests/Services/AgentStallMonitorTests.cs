using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentStallMonitor"/>.
///
/// All timing-sensitive tests inject <see cref="FakeTimeProvider"/> so they are
/// deterministic — no wall-clock waits, no CI flakiness.
///
/// Pattern: create a <see cref="SignalingFakeTimeProvider"/>, await its
/// <c>FirstTimerRegistered</c> task to know when the monitor has registered its
/// first <c>Task.Delay</c> callback with FakeTimeProvider, then call
/// <c>fakeTime.Advance()</c> to unblock it. This is fully race-free because the
/// timer registration happens synchronously inside <c>Task.Delay</c>'s call to
/// <c>CreateTimer</c> — so by the time the signal fires, the timer is guaranteed
/// to be queued.
/// </summary>
public class AgentStallMonitorTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;

    public AgentStallMonitorTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        };
    }

    /// <summary>
    /// Wraps <see cref="FakeTimeProvider"/> and signals <see cref="FirstTimerRegistered"/>
    /// the first time <see cref="CreateTimer"/> is called. This lets tests wait until the
    /// monitor has registered its polling timer before advancing fake time, eliminating the
    /// race that existed when a fixed-duration <c>Task.Delay</c> was used for synchronisation.
    /// </summary>
    private sealed class SignalingFakeTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly TaskCompletionSource _firstTimer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first <c>CreateTimer</c> call has been made.</summary>
        public Task FirstTimerRegistered => _firstTimer.Task;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            _firstTimer.TrySetResult();
            return timer;
        }
    }

    // ── Legacy fallback ────────────────────────────────────────────────────────

    /// <summary>
    /// Legacy fallback yield — kept for tests that do not need to advance fake time
    /// and therefore have no race to guard against. Not used by timing-sensitive tests.
    /// </summary>
    private static async Task YieldToMonitorAsync() => await Task.Delay(200);

    // ── DetectsProcessDeath ────────────────────────────────────────────────────

    [Fact]
    public async Task DetectsProcessDeath_LogsErrorWithPhaseContext()
    {
        var fakeTime = new FakeTimeProvider();

        // Large intervals so the kill/warning paths never fire — only process death matters here
        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 42, IsProcessAlive = false });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Test phase", null, _mockLogger.Object, CancellationToken.None,
            timeProvider: fakeTime);

        // Let the monitor loop start and reach its first Delay
        await YieldToMonitorAsync();
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Wait for the monitor to enqueue the death message
        await WaitForChatHistoryAsync(_run);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Test phase") &&
            c.Content.Contains("agent process is no longer alive") &&
            c.Content.Contains("42"));
    }

    // ── DetectsSilence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectsSilence_LogsWarningWithPhaseContext()
    {
        var fakeTime = new FakeTimeProvider();

        // LastOutputTime 3 minutes before fake "now" — already silent
        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromMinutes(2), // silence threshold: 2m
            AgentTimeout = TimeSpan.FromHours(1)            // kill threshold way out
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                // Already 3 minutes silent relative to fake "now"
                LastOutputTime = fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-3)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Code review agent 'Correctness'", null, _mockLogger.Object,
            CancellationToken.None, timeProvider: fakeTime);

        // After one poll tick: silence=3m > StallWarningInterval=2m.
        // lastWarnTime is initialised to fake-now, so timeSinceLastWarn = 1m after advancing.
        // We need timeSinceLastWarn >= StallWarningInterval=2m, so advance 2m total.
        await YieldToMonitorAsync();
        fakeTime.Advance(TimeSpan.FromMinutes(2)); // poll tick + satisfies timeSinceLastWarn check
        await WaitForChatHistoryAsync(_run);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Code review agent 'Correctness'") &&
            c.Content.Contains("no output for"));
    }

    // ── KillsAfterHardTimeout ──────────────────────────────────────────────────

    [Fact]
    public async Task KillsAfterHardTimeout_CallsKillAsync()
    {
        var fakeTime = new FakeTimeProvider();
        var signalingTime = new SignalingFakeTimeProvider(fakeTime);

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromHours(1), // suppress warnings
            AgentTimeout = TimeSpan.FromMinutes(5)        // kill after 5m silence
        };

        // Agent has been silent for 10 minutes relative to fake "now" — already past kill threshold
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-10)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        var killCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);
        _mockAgent.Setup(a => a.KillAsync())
            .Callback(() => killCalled.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Stuck agent", null, _mockLogger.Object, CancellationToken.None,
            timeProvider: signalingTime);

        // Wait until the monitor has registered its timer with FakeTimeProvider, then advance
        await signalingTime.FirstTimerRegistered;

        // Advance 1 minute → poll tick fires, silence = 10m+1m = 11m > AgentTimeout=5m → KillAsync called
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // KillAsync runs asynchronously after the timer fires; WaitForChatHistoryAsync covers the wait
        await WaitForChatHistoryAsync(_run);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        _mockAgent.Verify(a => a.KillAsync(), Times.Once);
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Forcefully terminating agent process"));
    }

    // ── CancelsCleanlyOnNormalCompletion ───────────────────────────────────────

    [Fact]
    public async Task CancelsCleanlyOnNormalCompletion()
    {
        var fakeTime = new FakeTimeProvider();

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = fakeTime.GetUtcNow().UtcDateTime
            });

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Fast agent", null, _mockLogger.Object, CancellationToken.None,
            timeProvider: fakeTime);

        result.ExitCode.Should().Be(0);
        _mockAgent.Verify(a => a.KillAsync(), Times.Never);
    }

    // ── MonitorAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorAsync_WrapsVoidAgentCall()
    {
        var fakeTime = new FakeTimeProvider();

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = false });

        var called = false;
        var tcs = new TaskCompletionSource();
        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(() => { called = true; return tcs.Task; });

        var task = AgentStallMonitor.MonitorAsync(
            _mockAgent.Object,
            () => _mockAgent.Object.EnsureSessionAsync("/ws", CancellationToken.None),
            _run, config, "Session warm-up", null, _mockLogger.Object, CancellationToken.None,
            timeProvider: fakeTime);

        // Advance repeatedly until the monitor loop has started, consumed the fake delay,
        // and enqueued the process-death message. This eliminates the race in YieldToMonitorAsync
        // where the Task.Run loop may not have reached its first Task.Delay within 200 ms on a
        // loaded CI runner.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            fakeTime.Advance(TimeSpan.FromMinutes(1));
        }

        tcs.SetResult();
        await task;

        called.Should().BeTrue();
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Session warm-up") &&
            c.Content.Contains("agent process is no longer alive"));
    }

    // ── Metrics counters ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSilenceWarning_EmitsStallWarningsCounter()
    {
        var fakeTime = new FakeTimeProvider();
        var signalingTime = new SignalingFakeTimeProvider(fakeTime);
        var factory = new TestMeterFactory();
        var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var warningsCounter = meter.CreateCounter<long>("quality_gate.stall.warnings", "{warning}");
        var killsCounter = meter.CreateCounter<long>("quality_gate.stall.kills", "{kill}");
        var deathsCounter = meter.CreateCounter<long>("quality_gate.stall.process_deaths", "{process_death}");
        var stallMetrics = new StallMonitorMetrics(warningsCounter, killsCounter, deathsCounter);

        using var warningCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.stall.warnings");

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromMinutes(2),
            AgentTimeout = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-3)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Quality gate retry agent (attempt 1)", null, _mockLogger.Object,
            CancellationToken.None, stallMetrics: stallMetrics, timeProvider: signalingTime);

        await signalingTime.FirstTimerRegistered;
        fakeTime.Advance(TimeSpan.FromMinutes(2));
        await WaitForMetricAsync(warningCollector, fakeTime, TimeSpan.FromMinutes(1));

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        var snapshot = warningCollector.GetMeasurementSnapshot();
        snapshot.Should().NotBeEmpty("at least one stall warning should have been emitted");
        snapshot.Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent)),
            "phase tag should be normalized to qgc_retry_agent");

        factory.Dispose();
    }

    [Fact]
    public async Task HandleKillTimeoutAsync_EmitsStallKillsCounter()
    {
        var fakeTime = new FakeTimeProvider();
        var factory = new TestMeterFactory();
        var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var warningsCounter = meter.CreateCounter<long>("quality_gate.stall.warnings", "{warning}");
        var killsCounter = meter.CreateCounter<long>("quality_gate.stall.kills", "{kill}");
        var deathsCounter = meter.CreateCounter<long>("quality_gate.stall.process_deaths", "{process_death}");
        var stallMetrics = new StallMonitorMetrics(warningsCounter, killsCounter, deathsCounter);

        using var killCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.stall.kills");

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromMinutes(5)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-10)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);
        _mockAgent.Setup(a => a.KillAsync()).Returns(Task.CompletedTask);
        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Quality gate retry agent (attempt 2)", null, _mockLogger.Object,
            CancellationToken.None, stallMetrics: stallMetrics, timeProvider: fakeTime);

        await YieldToMonitorAsync();
        fakeTime.Advance(TimeSpan.FromMinutes(1));
        await WaitForMetricAsync(killCollector, fakeTime, TimeSpan.FromMinutes(1));

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        var snapshot = killCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle("exactly one kill event should have been emitted");
        snapshot.Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent)),
            "phase tag should be normalized to qgc_retry_agent");

        factory.Dispose();
    }

    [Fact]
    public async Task HandleProcessDeath_EmitsStallProcessDeathsCounter()
    {
        var fakeTime = new FakeTimeProvider();
        var factory = new TestMeterFactory();
        var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var warningsCounter = meter.CreateCounter<long>("quality_gate.stall.warnings", "{warning}");
        var killsCounter = meter.CreateCounter<long>("quality_gate.stall.kills", "{kill}");
        var deathsCounter = meter.CreateCounter<long>("quality_gate.stall.process_deaths", "{process_death}");
        var stallMetrics = new StallMonitorMetrics(warningsCounter, killsCounter, deathsCounter);

        using var deathCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.stall.process_deaths");

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMinutes(1),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 99, IsProcessAlive = false });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Quality gate retry agent (attempt 3)", null, _mockLogger.Object,
            CancellationToken.None, stallMetrics: stallMetrics, timeProvider: fakeTime);

        await YieldToMonitorAsync();
        fakeTime.Advance(TimeSpan.FromMinutes(1));
        await WaitForMetricAsync(deathCollector, fakeTime, TimeSpan.FromMinutes(1));

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        var snapshot = deathCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle("exactly one process death event should have been emitted");
        snapshot.Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent)),
            "phase tag should be normalized to qgc_retry_agent");

        factory.Dispose();
    }

    // ── Polling helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Waits up to 15 seconds for the monitor to enqueue a ChatHistory entry.
    /// The wait is cheap because the monitor fires immediately after <c>fakeTime.Advance</c>
    /// unblocks its <c>Delay</c> — this loop typically exits on the first or second iteration.
    /// The 15-second cap (up from 5s) guards against ThreadPool scheduling delays on loaded CI
    /// runners where the monitor's Task.Run continuation may be queued behind other work items.
    /// </summary>
    private static async Task WaitForChatHistoryAsync(PipelineRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(5);
    }

    /// <summary>
    /// Waits up to 15 seconds for at least one measurement to appear in the collector.
    /// </summary>
    private static async Task WaitForMetricAsync<T>(MetricCollector<T> collector)
        where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (collector.GetMeasurementSnapshot().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
    }

    /// <summary>
    /// Waits up to 15 seconds for at least one measurement to appear in the collector,
    /// periodically re-advancing <paramref name="fakeTime"/> by <paramref name="advancePerTick"/>
    /// so the monitor loop is unblocked even if it had not yet reached its first Delay
    /// when the initial advance was called. This eliminates the race in the metrics tests
    /// where a single upfront Advance can be a no-op if the Task.Run loop hasn't started yet.
    /// </summary>
    private static async Task WaitForMetricAsync<T>(
        MetricCollector<T> collector,
        FakeTimeProvider fakeTime,
        TimeSpan advancePerTick)
        where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (collector.GetMeasurementSnapshot().Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            if (collector.GetMeasurementSnapshot().Count == 0)
                fakeTime.Advance(advancePerTick);
        }
    }
}
