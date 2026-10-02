using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Tests for the leader-gate check inside the <c>workitems_by_status</c> observable gauge
/// callback registered by <see cref="WorkItemCountsService"/>.
///
/// Issue #2980: The callback previously captured <c>_cachedMeasurements</c> directly without
/// a leader check. A non-leader replica that previously held leadership would continue to emit
/// its stale cached measurements. The fix adds a leader-gate check to the callback closure
/// so that only the current leader emits measurements.
///
/// Architecture note: <see cref="WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback"/>
/// uses <c>Interlocked.CompareExchange</c> — only the first-registered callback wins per process.
/// These tests run in [Collection("Metrics")] to serialize with other tests that use the same
/// static <see cref="WorkDistributionTelemetry.Meter"/>.
/// </summary>
[Collection("Metrics")]
public sealed class WorkItemsByStatusLeaderGateTests : IDisposable
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public WorkItemsByStatusLeaderGateTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemCountsService>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    public void Dispose() { }

    /// <summary>
    /// When a replica was the leader (populated cache), then loses leadership, the gauge
    /// callback must return empty measurements — not the stale cached data.
    ///
    /// This tests the scenario that was broken before issue #2980: a replica that held
    /// leadership and populated _cachedMeasurements, then lost leadership, would continue
    /// to emit its last-seen measurements indefinitely.
    ///
    /// Also verifies the inverse: while leader, measurements are emitted correctly.
    /// </summary>
    [Fact]
    public async Task GaugeCallback_EmitsMeasurementsWhileLeader_ThenReturnsEmptyAfterLoss()
    {
        // TODO [WARNING]: WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback uses
        // Interlocked.CompareExchange — only the first-registered callback wins per process lifetime.
        // If another test in this [Collection("Metrics")] group (or a prior test class run) has
        // already registered a callback, this WorkItemCountsService constructor's registration is
        // silently ignored. InvokeRegisteredCallback then invokes the previously registered callback
        // from a different service instance, making the leader-gate assertions unreliable.
        // The Dispose() method below is a no-op and does not reset _workItemsByStatusCallback.
        // Fix: expose a reset mechanism in WorkDistributionTelemetry (e.g. an internal
        // ResetWorkItemsByStatusCallbackForTesting() method) and call it in Dispose() here.

        // Arrange: start as leader
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemCountsResponseDto(
                [new WorkItemCountDto("Running", "dotnet", 5)],
                null));

        var svc = new WorkItemCountsService(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        // TODO [WARNING]: Task.Delay(200) is a time-based synchronisation with no fallback for slow
        // CI runners. If the system is under load, 200 ms may elapse before _cachedMeasurements is
        // populated (poll interval is 50 ms, but scheduling jitter can delay the first tick).
        // This causes readingsWhileLeader to be empty and the first assertion fails non-deterministically.
        // Fix: use SpinWait.SpinUntil(() => InvokeRegisteredCallback().Any(), timeout: TimeSpan.FromSeconds(5))
        // or a similar polling helper to wait for the cache to be populated before asserting.
        await Task.Delay(200, CancellationToken.None); // allow at least one poll tick while leader

        // Act (part 1): verify gauge emits measurements while leader
        var readingsWhileLeader = InvokeRegisteredCallback();
        readingsWhileLeader.Should().NotBeEmpty(
            "gauge callback must emit measurements while the replica is the leader and cache is populated");

        // Act (part 2): lose leadership
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(false);

        // Assert: callback now returns empty — no stale data
        var readingsAfterLoss = InvokeRegisteredCallback();
        readingsAfterLoss.Should().BeEmpty(
            "after losing leadership the gauge callback must return no measurements; " +
            "emitting stale cached data would cause duplicate series when the new leader " +
            "starts emitting (issue #2980 acceptance criterion: exactly one series per status/agent_selector)");

        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
        svc.Dispose();
    }

    /// <summary>
    /// Invokes the currently registered workitems_by_status callback by reading the
    /// observable gauge via a fresh MeterListener. Returns the measurements emitted.
    /// </summary>
    private static List<Measurement<long>> InvokeRegisteredCallback()
    {
        var results = new List<Measurement<long>>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.workitems_by_status")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "workdistribution.workitems_by_status")
                results.Add(new Measurement<long>(measurement, tags));
        });
        listener.Start();
        listener.RecordObservableInstruments();

        return results;
    }
}
