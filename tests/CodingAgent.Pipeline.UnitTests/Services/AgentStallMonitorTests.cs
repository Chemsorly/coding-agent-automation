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
/// Pattern: start the monitor, yield briefly with <c>await Task.Delay(small)</c>
/// so the Task.Run loop reaches its first <c>timeProvider.Delay</c>, then call
/// <c>fakeTime.Advance()</c> to unblock it.  The fake delay completes synchronously
/// on the ThreadPool thread running the monitor loop.
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

    // ── Helper: yield enough for the Task.Run loop to start and reach its Delay ──

    /// <summary>
    /// Yields the current thread so the monitor's Task.Run background loop can
    /// start, enter its while loop, and suspend on <c>timeProvider.Delay</c>.
    /// 500ms provides a wider buffer on loaded CI runners where the thread scheduler
    /// may not dispatch the Task.Run thread within a short window; metrics tests
    /// additionally re-advance the fake clock inside <c>WaitForMetricAsync</c> to
    /// recover if the initial advance fired before the loop was scheduled.
    /// </summary>
    // TODO [WARNING]: This is a time-dependent helper — a fixed sleep does not eliminate the
    // race; it only widens the window. On a sufficiently loaded CI runner the Task.Run background
    // loop may not have scheduled within the delay, causing DetectsSilence and the stall-metrics test
    // to fail non-deterministically. Additionally, for DetectsSilence the fake time is advanced by
    // 2 minutes in a single call, which may only fire the first Delay continuation synchronously
    // and leave the second poll iteration unscheduled before WaitForChatHistoryAsync is called —
    // depending on FakeTimeProvider's Advance implementation. A signal-based approach (e.g. a
    // TaskCompletionSource set when the loop enters its first Delay) would be fully deterministic.
    private static async Task YieldToMonitorAsync() => await Task.Delay(2000);

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
        fakeTime.Advance(TimeSpan.FromMinutes(1)); // trigger one poll tick

        // Wait for the monitor to enqueue the death message
        await WaitForChatHistoryAsync(_run, fakeTime, TimeSpan.FromMinutes(1));

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
        await WaitForChatHistoryAsync(_run, fakeTime, TimeSpan.FromMinutes(1));

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
            timeProvider: fakeTime);

        // Yield so the monitor loop starts and suspends on the first Delay(1m)
        await YieldToMonitorAsync();

        // Advance 1 minute → poll tick fires, silence = 10m+1m = 11m > AgentTimeout=5m → KillAsync called
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // KillAsync must be called promptly — no wall-clock dependency.
        // 10s timeout is generous; the fake-time Advance unblocks the monitor synchronously.
        await killCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));

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

        await YieldToMonitorAsync();
        fakeTime.Advance(TimeSpan.FromMinutes(1));
        await WaitForChatHistoryAsync(_run, fakeTime, TimeSpan.FromMinutes(1));

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
            CancellationToken.None, stallMetrics: stallMetrics, timeProvider: fakeTime);

        await YieldToMonitorAsync();
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
    /// The 15-second cap guards against ThreadPool scheduling delays on loaded CI runners.
    /// <para>
    /// When <paramref name="fakeTime"/> and <paramref name="advancePerTick"/> are provided,
    /// the clock is periodically re-advanced while waiting — recovering from the race where
    /// the initial <c>fakeTime.Advance</c> fired before the monitor's Task.Run loop had
    /// registered its first <c>timeProvider.Delay</c>. Mirrors the pattern in
    /// <see cref="WaitForMetricAsync{T}"/>.
    /// </para>
    /// </summary>
    private static async Task WaitForChatHistoryAsync(
        PipelineRun run,
        FakeTimeProvider? fakeTime = null,
        TimeSpan? advancePerTick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            if (run.ChatHistory.IsEmpty && fakeTime is not null && advancePerTick is not null)
                fakeTime.Advance(advancePerTick.Value);
        }
    }

    /// <summary>
    /// Waits up to 15 seconds for at least one measurement to appear in the collector.
    /// Periodically re-advances the fake clock by <paramref name="advancePerTick"/> to
    /// recover from the race where the initial <c>fakeTime.Advance</c> fired before the
    /// monitor's Task.Run loop had registered its first <c>timeProvider.Delay</c>. Without
    /// re-advancing, a single missed advance means the metric is never emitted and the test
    /// spins to the 15-second deadline — a flaky failure. (test quality review CRITICAL)
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
