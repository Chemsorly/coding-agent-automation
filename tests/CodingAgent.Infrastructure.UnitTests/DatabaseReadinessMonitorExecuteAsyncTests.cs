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
/// Note: the first probe in every test is preceded by a hardcoded 2-second
/// startup delay inside <c>ExecuteAsync</c>. Combined with a 1-second Npgsql
/// connect-timeout, each failure-path test takes a minimum of ~3 s. The
/// overall test timeout guards against indefinite hangs.
/// </para>
/// </summary>
public sealed class DatabaseReadinessMonitorExecuteAsyncTests
{
    // Non-routable IP + short connect-timeout: Npgsql always fails quickly.
    // Npgsql 10 uses "Timeout" (not the deprecated "ConnectTimeout") for the TCP connect timeout.
    private const string AlwaysFailConnectionString =
        "Host=127.0.0.2;Port=5432;Database=db;Username=u;Password=p;Timeout=1";

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
        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50),
            startupDelay: TimeSpan.Zero,
            probeOverride: _ => Task.FromException(new Exception("Simulated DB probe failure")));

        // Overall guard: if the state never flips something is very wrong.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.StartAsync(cts.Token);

        // Poll until the monitor marks the state unhealthy.
        // With startupDelay=Zero and an instantly-failing probe, this completes within milliseconds.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (healthState.IsDatabaseHealthy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);

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

        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50));

        // Overall guard against indefinite hang.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.StartAsync(cts.Token);

        // Wait until the first probe has failed and MarkUnhealthy has been called.
        // At that point we know the loop body has run and we are inside (or about to enter)
        // the inter-probe Task.Delay — cancelling now exercises the in-loop cancel paths.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (healthState.IsDatabaseHealthy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);

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

        var sut = new DatabaseReadinessMonitor(
            healthState,
            AlwaysFailConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50),
            startupDelay: TimeSpan.Zero,
            probeOverride: _ => Task.FromException(new Exception("Simulated DB probe failure")));

        // Overall guard against indefinite hang.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.StartAsync(cts.Token);

        // Wait until the "still unreachable" log appears, confirming the else branch ran.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!sink.Events.Any(e => e.MessageTemplate.Text.Contains("Database still unreachable"))
               && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);

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
