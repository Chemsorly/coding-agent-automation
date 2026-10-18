using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// MeterListener-based tests verifying that <c>agent.jobs.active</c>,
/// <c>agent.connections.total</c>, <c>workdistribution.credential_pool_available</c>
/// and <c>workdistribution.credential_pool_claimed</c> are emitted only by the
/// Scheduler leader and only after a successful poll.
///
/// Placed in [Collection("Metrics")] to serialize against other MeterListener tests
/// in this assembly, preventing cross-talk through the process-global static meters.
/// </summary>
[Collection("Metrics")]
public sealed class WorkItemCountsServiceAgentGaugeTests
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    // Reflection handles to reset the four static callback fields between tests so that
    // each test is fully self-contained regardless of execution order.
    private static readonly FieldInfo ActiveJobsCallbackField =
        typeof(PipelineTelemetry)
            .GetField("_activeJobsCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_activeJobsCallback field not found on PipelineTelemetry — was it renamed?");

    private static readonly FieldInfo TotalConnectionsCallbackField =
        typeof(PipelineTelemetry)
            .GetField("_totalConnectionsCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_totalConnectionsCallback field not found on PipelineTelemetry — was it renamed?");

    private static readonly FieldInfo CredentialPoolAvailableCallbackField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolAvailableCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_credentialPoolAvailableCallback field not found on WorkDistributionTelemetry — was it renamed?");

    private static readonly FieldInfo CredentialPoolClaimedCallbackField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolClaimedCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_credentialPoolClaimedCallback field not found on WorkDistributionTelemetry — was it renamed?");

    /// <summary>
    /// Resets all four static callback fields to null so the next test starts from a clean state.
    /// This is the same pattern used by <see cref="WorkDistributionTelemetryCredentialPoolGaugeTests"/>.
    /// </summary>
    private static void ResetStaticCallbacks()
    {
        ActiveJobsCallbackField.SetValue(null, null);
        TotalConnectionsCallbackField.SetValue(null, null);
        CredentialPoolAvailableCallbackField.SetValue(null, null);
        CredentialPoolClaimedCallbackField.SetValue(null, null);
    }

    public WorkItemCountsServiceAgentGaugeTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemCountsService>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        // Default: counts endpoint returns empty
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemCountsResponseDto(Array.Empty<WorkItemCountDto>(), null));
    }

    private WorkItemCountsService CreateService(ILeaderGate? gate = null)
        => new WorkItemCountsService(
            _mockClient.Object,
            gate ?? _mockLeaderGate.Object,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AgentEntryDto MakeAgent(AgentStatus status) =>
        new AgentEntryDto
        {
            AgentId = new AgentId(Guid.NewGuid().ToString()),
            ConnectionId = Guid.NewGuid().ToString(),
            Hostname = "test-host",
            Labels = Array.Empty<string>(),
            Status = status,
            RegisteredAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Reads the current measurements of an ObservableGauge using a fresh MeterListener.
    /// Returns the list of values emitted on RecordObservableInstruments().
    /// </summary>
    private static List<T> ReadGaugeValues<T>(string meterName, string instrumentName) where T : struct
    {
        var results = new List<T>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((instrument, value, _, _) =>
        {
            if (instrument.Name == instrumentName)
                results.Add(value);
        });
        listener.Start();
        listener.RecordObservableInstruments();

        return results;
    }

    // ── Leader emits ──────────────────────────────────────────────────────

    /// <summary>
    /// After a successful poll on the leader, all four gauges must emit the correct
    /// values derived from the faked API responses.
    /// </summary>
    [Fact]
    public async Task Leader_AfterSuccessfulPoll_EmitsAllFourGauges()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);

        // 3 agents: 2 busy, 1 idle → active=2, total=3
        var agents = new[]
        {
            MakeAgent(AgentStatus.Busy),
            MakeAgent(AgentStatus.Busy),
            MakeAgent(AgentStatus.Idle),
        };
        _mockClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(agents);

        // Pool: 5 total, 3 available, 2 claimed
        _mockClient
            .Setup(c => c.GetCredentialPoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialPoolStatus(5, 3, 2));

        var polled = new CallSignal();
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ReturnsAsync(new WorkItemCountsResponseDto(Array.Empty<WorkItemCountDto>(), null));

        var svc = CreateService();
        await BackgroundServiceRunner.RunUntilAsync(svc, polled.Reached,
            "the leader must poll at least once");

        // Read all four gauges
        var activeJobs = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active");
        var totalConnections = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total");
        var poolAvailable = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_available");
        var poolClaimed = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_claimed");

        activeJobs.Should().ContainSingle("leader must emit agent.jobs.active after poll")
            .Which.Should().Be(2L, "2 agents are Busy");

        totalConnections.Should().ContainSingle("leader must emit agent.connections.total after poll")
            .Which.Should().Be(3L, "3 agents total");

        poolAvailable.Should().ContainSingle("leader must emit credential_pool_available after poll")
            .Which.Should().Be(3, "3 PVCs are available");

        // TODO [WARNING]: The pool=kiro tag on credential_pool_available and credential_pool_claimed is
        // not asserted here. ReadGaugeValues<T> discards tag data (the SetMeasurementEventCallback lambda
        // captures only the scalar value). A regression that drops or renames the "pool" tag in the
        // production callback in WorkItemCountsService would not be caught. Fix: extend ReadGaugeValues<T>
        // (or add an overload) to capture tags, and assert both ("pool", "kiro") for available and claimed.
        // The instrument unit ({job}, {connection}, {pvc}) and description are not asserted either, although
        // "names, units and meters unchanged" is an acceptance criterion; `instrument` is already available
        // in the MeterListener callback. A fixture with a Disconnected agent (e.g. total=4, active=2) would
        // also catch a `Status != Idle` mutation of the busy count.
        // TODO [WARNING]: There are no zero/empty boundary tests. CredentialPoolExhausted fires on
        // `workdistribution_credential_pool_available == 0`, so the leader must emit an explicit 0 for
        // Available=0 (and 0 for an empty agent list); a regression that omits zero-valued measurements or
        // treats 0 as "no data" would silence that alert without failing anything here. A null API body is
        // untested too (see the TODO in WorkItemCountsService.UpdateMeasurementsAsync). Fix: add cases for
        // Available=0, an empty agent list and a null body.

        poolClaimed.Should().ContainSingle("leader must emit credential_pool_claimed after poll")
            .Which.Should().Be(2, "2 PVCs are claimed");
    }

    // ── Non-leader is silent ──────────────────────────────────────────────

    /// <summary>
    /// A non-leader must not emit any measurements for the four gauges even when the
    /// service is running.
    /// </summary>
    [Fact]
    public async Task NonLeader_EmitsNothing()
    {
        // Non-leader: IsLeader returns false, so UpdateMeasurementsAsync skips the API call.
        // We need a signal: check leadership twice to know the service loop has iterated.
        var secondCheck = new CallSignal(2);
        _mockLeaderGate
            .SetupGet(g => g.IsLeader)
            .Callback(() => secondCheck.Hit())
            .Returns(false);

        // TODO [WARNING]: IsLeader is accessed multiple times per service loop iteration — once in
        // UpdateMeasurementsAsync's gate check, and potentially once each in the four registered gauge
        // callbacks if OTel collection fires during the test. This means secondCheck.Reached can be
        // satisfied from a single OTel collection cycle triggering the callbacks, without UpdateMeasurementsAsync
        // having run even once. The test therefore does not reliably verify that the non-leader path in
        // UpdateMeasurementsAsync was exercised — only that IsLeader was read twice. A more robust approach
        // would signal from a dedicated hook inside UpdateMeasurementsAsync after the leadership check, or
        // count API call invocations (which should be zero on a non-leader).

        var svc = CreateService();
        await BackgroundServiceRunner.RunUntilAsync(svc, secondCheck.Reached,
            "the non-leader must iterate the service loop at least twice");

        var activeJobs = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active");
        var totalConnections = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total");
        var poolAvailable = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_available");
        var poolClaimed = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_claimed");

        activeJobs.Should().BeEmpty("non-leader must emit nothing for agent.jobs.active");
        totalConnections.Should().BeEmpty("non-leader must emit nothing for agent.connections.total");
        poolAvailable.Should().BeEmpty("non-leader must emit nothing for credential_pool_available");
        poolClaimed.Should().BeEmpty("non-leader must emit nothing for credential_pool_claimed");

        // TODO [WARNING]: This test does not verify that the two new API calls are skipped on a
        // non-leader. WorkItemCountsServiceTests.WhenNotLeader_DoesNotCallApi only verifies
        // GetWorkItemCountsAsync, so nothing pins GetAgentsAsync / GetCredentialPoolAsync as never
        // called. Fix: add _mockClient.Verify(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()),
        // Times.Never) and the same for GetCredentialPoolAsync.
    }

    /// <summary>
    /// The leader-gate check inside each of the four gauge callbacks must silence the gauges the
    /// moment leadership is lost, even though the caches still hold the last successful poll.
    /// A replica that polled as leader and then lost the lease must not keep exporting its cached
    /// values, or it would double-count against the new leader. Regaining leadership makes the
    /// cached values visible again, which shows the gate alone — not an empty cache — is what
    /// silences the gauges in between.
    /// </summary>
    [Fact]
    public async Task Leader_ThenLosesLeadership_GaugesGoSilentDespiteCachedValues()
    {
        var isLeader = true;
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(() => Volatile.Read(ref isLeader));

        _mockClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAgent(AgentStatus.Busy), MakeAgent(AgentStatus.Idle) });
        _mockClient
            .Setup(c => c.GetCredentialPoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialPoolStatus(5, 3, 2));

        var polled = new CallSignal();
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ReturnsAsync(new WorkItemCountsResponseDto(Array.Empty<WorkItemCountDto>(), null));

        // RunUntilAsync awaits StopAsync before returning, so the poll (and its cache writes) has
        // completed and no further poll can run while the gate is flipped below.
        var svc = CreateService();
        await BackgroundServiceRunner.RunUntilAsync(svc, polled.Reached,
            "the leader must poll at least once");

        ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active")
            .Should().ContainSingle("the leader emits after a successful poll").Which.Should().Be(1L);

        // Lose leadership: the caches are untouched, only the gate changes.
        Volatile.Write(ref isLeader, false);

        ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active")
            .Should().BeEmpty("agent.jobs.active must go silent once leadership is lost");
        ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total")
            .Should().BeEmpty("agent.connections.total must go silent once leadership is lost");
        ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_available")
            .Should().BeEmpty("credential_pool_available must go silent once leadership is lost");
        ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_claimed")
            .Should().BeEmpty("credential_pool_claimed must go silent once leadership is lost");

        // Regain leadership: the cached values are visible again, so the gate was the only thing
        // silencing the gauges.
        Volatile.Write(ref isLeader, true);

        ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active")
            .Should().ContainSingle().Which.Should().Be(1L);
        ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total")
            .Should().ContainSingle().Which.Should().Be(2L);
        ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_available")
            .Should().ContainSingle().Which.Should().Be(3);
        ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_claimed")
            .Should().ContainSingle().Which.Should().Be(2);
    }

    // ── No poll, no value ─────────────────────────────────────────────────

    /// <summary>
    /// Before the service starts (i.e. no poll has fired), all four gauges must emit nothing.
    /// The observable gauge instruments exist (registered at construction) but the callbacks
    /// return empty when no cache data is available.
    /// </summary>
    [Fact]
    public void BeforeFirstPoll_EmitsNothing()
    {
        // Arrange: reset all four static callbacks to null so this test is self-contained
        // regardless of execution order. A prior test may have left non-null callbacks
        // registered (e.g. from a service instance whose caches happen to be empty because
        // its poll failed). Without the reset, constructing a new service below would
        // overwrite the callbacks with new closures, but the assertions would be testing the
        // new closures' initial empty state — the correct behavior — only incidentally.
        // Resetting first ensures this test catches any future change that initialises the
        // caches with non-null values at construction time.
        ResetStaticCallbacks();

        // Construct the service but do NOT start it → no polls have fired
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _ = CreateService(); // registers callbacks but no poll has fired

        var activeJobs = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active");
        var totalConnections = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total");
        var poolAvailable = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_available");
        var poolClaimed = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_claimed");

        activeJobs.Should().BeEmpty("agent.jobs.active must emit nothing before first poll");
        totalConnections.Should().BeEmpty("agent.connections.total must emit nothing before first poll");
        poolAvailable.Should().BeEmpty("credential_pool_available must emit nothing before first poll");
        poolClaimed.Should().BeEmpty("credential_pool_claimed must emit nothing before first poll");
    }

    /// <summary>
    /// Calling <see cref="PipelineTelemetry.RegisterAgentGaugeCallbacks"/> with non-null callbacks
    /// and then triggering a collection cycle must reach those callbacks and emit the values they
    /// return. This test directly exercises the <c>observeValues</c> lambdas to confirm that
    /// <see cref="System.Threading.Volatile.Read{T}"/> does not suppress a non-null value written
    /// by the same thread before the gauge is read (acceptance criterion #3).
    /// </summary>
    [Fact]
    public void RegisterAgentGaugeCallbacks_WithNonNullCallbacks_CallbacksAreReachedOnCollection()
    {
        // TODO [WARNING]: This test verifies that the gauge wiring is correct end-to-end (callbacks
        // are registered and reached on a collection cycle), but it does not serve as a regression
        // guard for the Volatile.Read fix specifically. Because registration and collection run on
        // the same thread, the C# memory model guarantees the thread always observes its own prior
        // writes regardless of volatile semantics — the test would also pass if both Volatile.Read
        // calls in PipelineTelemetry.cs were reverted to bare field access. The ARM/AArch64
        // acquire/release ordering cannot be reproduced on x86-64 test runners; the true guard
        // against this class of regression is a code review that checks Volatile.Read is present
        // in the observeValues lambdas whenever Volatile.Write is used for registration.

        // Arrange: start from a clean state regardless of test execution order.
        ResetStaticCallbacks();

        var activeJobsMeasurement = new Measurement<long>(7L);
        var totalConnectionsMeasurement = new Measurement<long>(3L);

        PipelineTelemetry.RegisterAgentGaugeCallbacks(
            activeJobsCallback: () => [activeJobsMeasurement],
            totalConnectionsCallback: () => [totalConnectionsMeasurement]);

        // Act: trigger an OTel collection cycle.
        var activeJobs = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active");
        var totalConnections = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total");

        // Assert: the callbacks were reached and emitted the expected values.
        activeJobs.Should().ContainSingle("the activeJobsCallback must be reached by RecordObservableInstruments")
            .Which.Should().Be(7L);
        totalConnections.Should().ContainSingle("the totalConnectionsCallback must be reached by RecordObservableInstruments")
            .Which.Should().Be(3L);
    }

    /// <summary>
    /// After a failed poll (API throws), all four gauges must reset to empty — no stale values.
    /// </summary>
    [Fact]
    public async Task AfterFailedPoll_EmitsNothing()
    {
        // TODO [WARNING]: Only GetWorkItemCountsAsync is made to fail; a faulting GetAgentsAsync or
        // GetCredentialPoolAsync is not covered. The test also does not assert that the gauges held values
        // after the first successful poll, so it cannot distinguish "reset after failure" from "never
        // populated", has no recovery case (fail, then succeed, then emit again), and `firstPoll` below is
        // created and hit but never awaited (dead code). Fix: fail each of the three endpoints in turn,
        // assert emission after the first poll, and add a fail-then-recover case. Because the three calls
        // share one catch block, a failure of the agents or pool endpoint alone also blanks
        // workitems_by_status and the oldest-pending-age gauge; no test pins that partial-failure behaviour.
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);

        // First poll succeeds with data
        var firstPoll = new CallSignal(1);
        var secondPoll = new CallSignal(2);

        _mockClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAgent(AgentStatus.Busy) });

        _mockClient
            .Setup(c => c.GetCredentialPoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialPoolStatus(1, 1, 0));

        var callCount = 0;
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // TODO [WARNING]: callCount is incremented without synchronization. This is safe
                // today because the mock callbacks are invoked sequentially within a single
                // await Task.WhenAll call, but would be fragile if the service were changed to
                // issue truly parallel HTTP calls. Use Interlocked.Increment if concurrency is added.
                callCount++;
                firstPoll.Hit();
                secondPoll.Hit();
                if (callCount >= 2) throw new HttpRequestException("simulated failure");
                return new WorkItemCountsResponseDto(Array.Empty<WorkItemCountDto>(), null);
            });

        // TODO [WARNING]: There is a race between the secondPoll signal firing and the cache reset
        // completing. secondPoll.Hit() is called inside the GetWorkItemCountsAsync lambda before the
        // exception propagates out of Task.WhenAll and the catch block runs the three Volatile.Write
        // resets. BackgroundServiceRunner.RunUntilAsync unblocks the moment secondPoll.Reached is true,
        // so the assertions below may run before _cachedAgentCounts/_cachedCredentialPoolCounts have
        // been reset to null, producing false failures under load. To guarantee the resets have completed
        // before asserting, signal from *after* the exception is caught (e.g. a per-failure hook) rather
        // than from inside the throwing lambda.

        var svc = CreateService();
        await BackgroundServiceRunner.RunUntilAsync(svc, secondPoll.Reached,
            "the leader must poll at least twice (first succeeds, second fails)");

        // After the second (failed) poll, all four caches should be reset to null
        var activeJobs = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.jobs.active");
        var totalConnections = ReadGaugeValues<long>(PipelineTelemetry.SourceName, "agent.connections.total");
        var poolAvailable = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_available");
        var poolClaimed = ReadGaugeValues<int>(WorkDistributionTelemetry.MeterName, "workdistribution.credential_pool_claimed");

        activeJobs.Should().BeEmpty("agent.jobs.active must emit nothing after a failed poll");
        totalConnections.Should().BeEmpty("agent.connections.total must emit nothing after a failed poll");
        poolAvailable.Should().BeEmpty("credential_pool_available must emit nothing after a failed poll");
        poolClaimed.Should().BeEmpty("credential_pool_claimed must emit nothing after a failed poll");
    }
}
