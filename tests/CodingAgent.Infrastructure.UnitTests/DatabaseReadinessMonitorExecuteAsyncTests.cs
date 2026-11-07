using AwesomeAssertions;
using CodingAgent.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Tests for the <see cref="DatabaseReadinessMonitor"/> background loop
/// (<c>ExecuteAsync</c>).  These tests drive the service through a real
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> lifecycle
/// (<c>StartAsync</c>/<c>StopAsync</c>) and do NOT require a running database.
///
/// <para>
/// Note: tests that reach a probe inject a failing one (<c>FailingProbe</c>) and a zero
/// startup delay, so no real connection is attempted. <c>BackgroundService.StartAsync</c>
/// returns before the first probe runs because the loop is dispatched to the thread pool,
/// so on a loaded runner that probe can start seconds later. These tests therefore wait for
/// the probe to be invoked instead of polling against a short wall-clock deadline;
/// <c>HangGuard</c> only bounds a genuine hang.
/// </para>
/// </summary>
public sealed class DatabaseReadinessMonitorExecuteAsyncTests
{
    // Non-routable IP + short connect-timeout: Npgsql always fails quickly.
    // Npgsql 10 uses "Timeout" (not the deprecated "ConnectTimeout") for the TCP connect timeout.
    private const string AlwaysFailConnectionString =
        "Host=127.0.0.2;Port=5432;Database=db;Username=u;Password=p;Timeout=1";

    // Bounds a genuine hang only. The tests wait for events and finish as soon as they fire, so this is
    // not a timing expectation; it is generous because the monitor loop runs on the (possibly starved) thread pool.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // ── AC1: probe-failure path ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FirstProbeFailure_MarksUnhealthyAndLogsWarning()
    {
        var healthState = new DatabaseHealthState();
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Use an injected failing probe and zero startup delay so the test is deterministic:
        // no real network call is made, and no timing assumptions about 127.0.0.2 reachability
        // or the TCP connect timeout are needed. The original test relied on 127.0.0.2:5432
        // failing within the poll window, which was not guaranteed on all CI environments
        // (e.g. when PostgreSQL happens to listen on 0.0.0.0:5432 on the runner, 127.0.0.2
        // is within the Linux loopback range and the probe would succeed).
        var probe = new FailingProbe(signalOnCall: 2);
        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50),
            startupDelay: TimeSpan.Zero,
            probeOverride: probe.FailAsync);

        // Hang guard: cancelling it stops the monitor and fails the wait below.
        using var cts = new CancellationTokenSource(HangGuard);
        await sut.StartAsync(cts.Token);

        // The second probe only starts after the first failure has been fully handled (Warning logged,
        // MarkUnhealthy called), so waiting for it needs no further polling.
        await probe.Reached.WaitAsync(cts.Token);

        await sut.StopAsync(CancellationToken.None);

        healthState.IsDatabaseHealthy.Should().BeFalse(
            "a failed probe must call MarkUnhealthy so /readyz returns 503");

        sink.Events.Should().Contain(
            e => e.Level == LogEventLevel.Warning
              && e.MessageTemplate.Text.Contains("Database connectivity lost"),
            "a Warning must be emitted on the first probe failure");
    }

    // ── AC3: shutdown / cancellation path ────────────────────────────────────

    /// <summary>
    /// Exercises cancellation during the 2-second startup delay, before the loop is entered.
    /// The three in-loop cancellation paths (probe OperationCanceledException, inter-probe
    /// Task.Delay cancellation, and the while-condition check) are covered by the
    /// <see cref="ExecuteAsync_CancellationDuringLoop_ExitsWithoutChangingHealthState"/> test below.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancellationTokenFired_ExitsWithoutChangingHealthState()
    {
        var healthState = new DatabaseHealthState();
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50));

        // Start, then stop immediately before the 2-second startup delay completes.
        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        healthState.IsDatabaseHealthy.Should().BeTrue(
            "cancellation before any probe must not alter the health state");

        sink.Events.Should().NotContain(
            e => e.Level == LogEventLevel.Warning,
            "no probe ran, so no Warning should be emitted");
    }

    /// <summary>
    /// Exercises the in-loop cancellation paths: waits for at least one probe to complete
    /// (confirming the loop body has been entered), then fires the cancellation token.
    /// This covers the <c>catch (OperationCanceledException) { break; }</c> handler on the
    /// inter-probe <c>Task.Delay</c> and the <c>while (!stoppingToken.IsCancellationRequested)</c>
    /// guard, neither of which the startup-delay cancellation test above can reach.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancellationDuringLoop_ExitsWithoutChangingHealthState()
    {
        var healthState = new DatabaseHealthState();
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var probe = new FailingProbe(signalOnCall: 2);
        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50),
            startupDelay: TimeSpan.Zero,
            probeOverride: probe.FailAsync);

        // Hang guard: cancelling it stops the monitor and fails the wait below.
        using var cts = new CancellationTokenSource(HangGuard);
        await sut.StartAsync(cts.Token);

        // Wait until the second probe starts: the first failure has been handled (MarkUnhealthy called) and
        // the loop has gone around once, so cancelling now exercises the in-loop cancel paths.
        await probe.Reached.WaitAsync(cts.Token);

        await sut.StopAsync(CancellationToken.None);

        healthState.IsDatabaseHealthy.Should().BeFalse(
            "cancellation after a failure must leave the health state as unhealthy");

        sink.Events.Should().NotContain(
            e => e.Level == LogEventLevel.Information,
            "cancellation must never call MarkHealthy and log recovery");
    }

    // ── Consecutive-failure path (else branch of exception handler) ───────────

    /// <summary>
    /// Exercises the <c>else</c> branch inside <c>ExecuteAsync</c>'s exception handler:
    /// when the DB is already unhealthy and the next probe also fails, the monitor logs
    /// "Database still unreachable" without calling <c>MarkUnhealthy</c> again.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ConsecutiveProbeFailures_LogsStillUnreachableWithoutChangingState()
    {
        var healthState = new DatabaseHealthState();
        // Simulate a prior failure: the state is already unhealthy when the monitor starts.
        healthState.MarkUnhealthy();

        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var probe = new FailingProbe(signalOnCall: 2);
        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50),
            startupDelay: TimeSpan.Zero,
            probeOverride: probe.FailAsync);

        // Hang guard: cancelling it stops the monitor and fails the wait below.
        using var cts = new CancellationTokenSource(HangGuard);
        await sut.StartAsync(cts.Token);

        // The second probe only starts after the first failure has been handled. The state was already
        // unhealthy, so that handling was the else branch that logs "still unreachable".
        await probe.Reached.WaitAsync(cts.Token);

        await sut.StopAsync(CancellationToken.None);

        healthState.IsDatabaseHealthy.Should().BeFalse(
            "consecutive probe failures must not alter the already-unhealthy state");

        sink.Events.Should().Contain(
            e => e.Level == LogEventLevel.Warning
              && e.MessageTemplate.Text.Contains("Database still unreachable"),
            "subsequent probe failures when already unhealthy must log the still-unreachable message");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Probe that always fails and completes <see cref="Reached"/> when it is invoked for the
    /// <paramref name="signalOnCall"/>-th time. The monitor loop is sequential, so the N-th invocation
    /// only happens after the failures of invocations 1..N-1 have been fully handled.
    /// </summary>
    private sealed class FailingProbe(int signalOnCall)
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Reached => _reached.Task;

        public Task FailAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == signalOnCall)
                _reached.TrySetResult();
            return Task.FromException(new Exception("Simulated DB probe failure"));
        }
    }

    /// <summary>
    /// Thread-safe Serilog sink that collects log events for assertion.
    /// The logger is injected directly into the SUT, so no global <c>Log.Logger</c>
    /// mutation is needed and no xunit collection serialisation is required.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _events = [];

        /// <summary>Returns a snapshot of all collected events, safe to enumerate after the call.</summary>
        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_lock) { return [.. _events]; } }
        }

        public void Emit(LogEvent logEvent) { lock (_lock) { _events.Add(logEvent); } }
    }
}
