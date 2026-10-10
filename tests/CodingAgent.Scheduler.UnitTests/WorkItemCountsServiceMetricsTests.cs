using System.Collections.Concurrent;
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
/// MeterListener-based tests for the agent and credential-pool gauge behaviour introduced by
/// issue #3555. These gauges are now emitted exclusively by the Scheduler leader via
/// <see cref="WorkItemCountsService"/>.
///
/// <para>
/// Four gauges are tested here:
/// <list type="bullet">
///   <item><c>agent.jobs.active</c> — on <c>PipelineTelemetry.Meter</c></item>
///   <item><c>agent.connections.total</c> — on <c>PipelineTelemetry.Meter</c></item>
///   <item><c>workdistribution.credential_pool_available</c> — on <c>WorkDistributionTelemetry.Meter</c></item>
///   <item><c>workdistribution.credential_pool_claimed</c> — on <c>WorkDistributionTelemetry.Meter</c></item>
/// </list>
/// </para>
///
/// <para>
/// All tests are in [Collection("Metrics")] to serialise against other MeterListener tests
/// in this assembly that share the same process-global static meters.
/// </para>
/// </summary>
[Collection("Metrics")]
public sealed class WorkItemCountsServiceMetricsTests : IDisposable
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    // Reflection handles for the credential-pool static fields.
    // Must be reset before each RecordObservableInstruments() call to isolate from other tests
    // in the [Collection("Metrics")] suite that leave _credentialPoolUpdated in an unknown state.
    private static readonly FieldInfo CredentialPoolUpdatedField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolUpdated", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_credentialPoolUpdated not found");

    private static readonly FieldInfo CredentialPoolAvailableField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolAvailable", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_credentialPoolAvailable not found");

    private static readonly FieldInfo CredentialPoolClaimedField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolClaimed", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_credentialPoolClaimed not found");

    // Reflection handle for the agent-gauge shouldEmit callback, so tests can reset it
    // to avoid interference with the registered callback from another WorkItemCountsService instance.
    private static readonly FieldInfo AgentShouldEmitField =
        typeof(PipelineTelemetry)
            .GetField("_agentShouldEmitCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_agentShouldEmitCallback not found");

    private static readonly FieldInfo AgentActiveCallbackField =
        typeof(PipelineTelemetry)
            .GetField("_agentActiveCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_agentActiveCallback not found");

    private static readonly FieldInfo AgentTotalCallbackField =
        typeof(PipelineTelemetry)
            .GetField("_agentTotalCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_agentTotalCallback not found");

    public WorkItemCountsServiceMetricsTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemCountsService>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        // Always provide default setups for the workitems method so the service doesn't throw.
        _mockClient.Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemCountsResponseDto([], null));
    }

    public void Dispose()
    {
        // Reset agent gauge callbacks so tests don't bleed into each other.
        // Because RegisterAgentGaugeCallbacks uses Interlocked.CompareExchange (first-wins),
        // a subsequent test that constructs a new WorkItemCountsService would have its callbacks
        // silently ignored unless we reset the fields here.
        AgentActiveCallbackField.SetValue(null, null);
        AgentTotalCallbackField.SetValue(null, null);
        AgentShouldEmitField.SetValue(null, null);
    }

    private void ResetCredentialPoolState()
    {
        CredentialPoolUpdatedField.SetValue(null, false);
        CredentialPoolAvailableField.SetValue(null, 0);
        CredentialPoolClaimedField.SetValue(null, 0);
    }

    private WorkItemCountsService CreatePoller() =>
        new WorkItemCountsService(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    // ── Test 1: Leader emits all four gauges after a successful poll ──────────

    /// <summary>
    /// On the leader, after the first successful poll, all four gauges must emit the values
    /// returned by the API mocks.
    /// </summary>
    [Fact]
    public async Task WhenLeader_AfterSuccessfulPoll_AllFourGaugesEmitExpectedValues()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);

        var agentPolled = new CallSignal();
        _mockClient
            .Setup(c => c.GetAgentCountsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => agentPolled.Hit())
            .ReturnsAsync(new AgentCountsResponseDto(Total: 5, Busy: 2));
        _mockClient
            .Setup(c => c.GetAgentCredentialPoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialPoolStatus(Total: 10, Available: 7, Claimed: 3));

        // Collect measurements for both meters.
        var agentReadings = new ConcurrentBag<(string Name, int Value)>();
        var poolReadings = new ConcurrentBag<(string Name, int Value)>();

        using var agentListener = new MeterListener();
        agentListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName &&
                (instrument.Name == "agent.jobs.active" || instrument.Name == "agent.connections.total"))
                listener.EnableMeasurementEvents(instrument);
        };
        agentListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            agentReadings.Add((instrument.Name, value)));
        agentListener.Start();

        using var poolListener = new MeterListener();
        poolListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                (instrument.Name == "workdistribution.credential_pool_available" ||
                 instrument.Name == "workdistribution.credential_pool_claimed"))
                listener.EnableMeasurementEvents(instrument);
        };
        poolListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            poolReadings.Add((instrument.Name, value)));
        poolListener.Start();

        // Reset credential pool state so we start clean regardless of other tests' residue.
        ResetCredentialPoolState();

        // TODO [WARNING]: The CallSignal fires inside GetAgentCountsAsync (before GetAgentCredentialPoolAsync
        // completes and before _agentMetricsPolled = true is written). If RecordObservableInstruments() is
        // called before _agentMetricsPolled is written, shouldEmit() returns false and the agent gauges emit
        // nothing, causing a spurious failure. The signal should fire after the entire successful poll
        // completes (i.e., after _agentMetricsPolled = true), for example by hooking on a post-poll
        // observable (e.g. the pool call, or a dedicated completion signal), or by adding a brief
        // stabilisation wait after agentPolled.Reached is signalled.
        await BackgroundServiceRunner.RunUntilAsync(CreatePoller(), agentPolled.Reached,
            "leader must poll agent counts");

        // Collect observable instruments synchronously from the test thread.
        agentReadings.Clear();
        poolReadings.Clear();
        agentListener.RecordObservableInstruments();
        poolListener.RecordObservableInstruments();

        // agent.jobs.active → Busy=2
        agentReadings.Should().Contain(r => r.Name == "agent.jobs.active" && r.Value == 2,
            "agent.jobs.active must emit the Busy count (2)");
        // agent.connections.total → Total=5
        agentReadings.Should().Contain(r => r.Name == "agent.connections.total" && r.Value == 5,
            "agent.connections.total must emit the Total count (5)");
        // credential_pool_available → Available=7
        poolReadings.Should().Contain(r => r.Name == "workdistribution.credential_pool_available" && r.Value == 7,
            "workdistribution.credential_pool_available must emit Available=7");
        // credential_pool_claimed → Claimed=3
        poolReadings.Should().Contain(r => r.Name == "workdistribution.credential_pool_claimed" && r.Value == 3,
            "workdistribution.credential_pool_claimed must emit Claimed=3");
    }

    // ── Test 2: Non-leader emits nothing ─────────────────────────────────────

    /// <summary>
    /// On a non-leader replica, all four gauges must emit no measurements.
    /// </summary>
    [Fact]
    public async Task WhenNonLeader_AllFourGaugesEmitNothing()
    {
        var secondCheck = new CallSignal(2);
        _mockLeaderGate.SetupGet(g => g.IsLeader)
            .Callback(() => secondCheck.Hit())
            .Returns(false);

        var agentReadings = new ConcurrentBag<(string Name, int Value)>();
        var poolReadings = new ConcurrentBag<(string Name, int Value)>();

        using var agentListener = new MeterListener();
        agentListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName &&
                (instrument.Name == "agent.jobs.active" || instrument.Name == "agent.connections.total"))
                listener.EnableMeasurementEvents(instrument);
        };
        agentListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            agentReadings.Add((instrument.Name, value)));
        agentListener.Start();

        using var poolListener = new MeterListener();
        poolListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                (instrument.Name == "workdistribution.credential_pool_available" ||
                 instrument.Name == "workdistribution.credential_pool_claimed"))
                listener.EnableMeasurementEvents(instrument);
        };
        poolListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            poolReadings.Add((instrument.Name, value)));
        poolListener.Start();

        // TODO [WARNING]: ResetCredentialPoolState() is called in Tests 1 and 3b but not here.
        // If a prior test in the [Collection("Metrics")] suite leaves _credentialPoolUpdated = true,
        // the pool listener will emit stale values and poolReadings.Should().BeEmpty() will fail spuriously.
        // Add ResetCredentialPoolState() here, before RecordObservableInstruments(), to isolate this test
        // from residue left by other tests — the non-leader service never calls UpdateCredentialPoolMetrics,
        // so the sentinel must be reset explicitly.
        ResetCredentialPoolState();

        await BackgroundServiceRunner.RunUntilAsync(CreatePoller(), secondCheck.Reached,
            "non-leader must keep checking leadership");

        agentReadings.Clear();
        poolReadings.Clear();
        agentListener.RecordObservableInstruments();
        poolListener.RecordObservableInstruments();

        agentReadings.Should().BeEmpty("non-leader must not emit agent gauge measurements");
        poolReadings.Should().BeEmpty("non-leader must not emit credential-pool gauge measurements");
    }

    // ── Test 3a: Before first poll, agent gauges emit nothing ─────────────────

    /// <summary>
    /// Immediately after construction (before any poll has completed), the agent gauges must
    /// emit no measurements — no stale 0s before the first data arrives.
    /// </summary>
    [Fact]
    public void BeforeFirstPoll_AgentGaugesEmitNothing()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);

        var agentReadings = new ConcurrentBag<(string Name, int Value)>();

        using var agentListener = new MeterListener();
        agentListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName &&
                (instrument.Name == "agent.jobs.active" || instrument.Name == "agent.connections.total"))
                listener.EnableMeasurementEvents(instrument);
        };
        agentListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            agentReadings.Add((instrument.Name, value)));
        agentListener.Start();

        // Construct the service but do NOT start it — _agentMetricsPolled stays false.
        _ = CreatePoller();

        agentReadings.Clear();
        agentListener.RecordObservableInstruments();

        // TODO [WARNING]: This test only checks the agent gauges (PipelineTelemetry.Meter).
        // The acceptance criterion says all four gauges must emit nothing before the first poll.
        // A pool listener should also be attached and poolReadings.Should().BeEmpty() asserted here
        // to cover workdistribution.credential_pool_available / _claimed. A process whose
        // _credentialPoolUpdated was set by a prior run would silently emit pool measurements even
        // before the first poll, and this test would not catch that.
        agentReadings.Should().BeEmpty(
            "agent gauges must emit nothing before the first successful poll — no stale 0s");
    }

    // ── Test 3b: After a failed poll, all four gauges emit nothing ────────────

    /// <summary>
    /// After a failed poll (API throws), all four gauges must emit nothing — the sentinels
    /// (_agentMetricsPolled and _credentialPoolUpdated) must be reset on the failure path.
    /// </summary>
    [Fact]
    public async Task AfterFailedPoll_AllFourGaugesEmitNothing()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);

        var warned = new CallSignal();
        _mockLogger.Setup(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()))
            .Callback(() => warned.Hit());

        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        // GetAgentCountsAsync and GetAgentCredentialPoolAsync should not be reached when
        // GetWorkItemCountsAsync throws, but keep the defaults in case ordering changes.
        _mockClient
            .Setup(c => c.GetAgentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCountsResponseDto(0, 0));
        _mockClient
            .Setup(c => c.GetAgentCredentialPoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialPoolStatus(0, 0, 0));

        var agentReadings = new ConcurrentBag<(string Name, int Value)>();
        var poolReadings = new ConcurrentBag<(string Name, int Value)>();

        using var agentListener = new MeterListener();
        agentListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName &&
                (instrument.Name == "agent.jobs.active" || instrument.Name == "agent.connections.total"))
                listener.EnableMeasurementEvents(instrument);
        };
        agentListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            agentReadings.Add((instrument.Name, value)));
        agentListener.Start();

        using var poolListener = new MeterListener();
        poolListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                (instrument.Name == "workdistribution.credential_pool_available" ||
                 instrument.Name == "workdistribution.credential_pool_claimed"))
                listener.EnableMeasurementEvents(instrument);
        };
        poolListener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            poolReadings.Add((instrument.Name, value)));
        poolListener.Start();

        // Seed _credentialPoolUpdated = true to simulate a prior successful poll —
        // the failure path must reset it to false.
        CredentialPoolUpdatedField.SetValue(null, true);
        CredentialPoolAvailableField.SetValue(null, 5);
        CredentialPoolClaimedField.SetValue(null, 2);
        // TODO [WARNING]: _agentMetricsPolled is NOT seeded to true here. The test therefore does not
        // verify that the failure path *resets* a previously-set sentinel — it only verifies that a
        // never-set sentinel stays false. To properly test the requirement, set _agentMetricsPolled = true
        // via reflection before running the poller (mirroring how _credentialPoolUpdated is seeded above),
        // then assert it was reset to false. Use the AgentShouldEmitField to access the backing field, or
        // add a dedicated FieldInfo for _agentMetricsPolled in this test class.

        // TODO [WARNING]: Only GetWorkItemCountsAsync is made to throw. This tests the case where the
        // first API call in UpdateMeasurementsAsync fails. If the call order changes (e.g., agent counts
        // polled before work-item counts), or if GetAgentCountsAsync / GetAgentCredentialPoolAsync throw
        // independently, the sentinels may not be reset and these assertions would pass while the failure
        // path was never exercised. Add separate test cases that make GetAgentCountsAsync throw (but
        // GetWorkItemCountsAsync succeeds) and GetAgentCredentialPoolAsync throw (but the earlier calls
        // succeed) to verify the catch path resets both sentinels regardless of which call throws.

        await BackgroundServiceRunner.RunUntilAsync(CreatePoller(), warned.Reached,
            "service must log a warning on API failure");

        agentReadings.Clear();
        poolReadings.Clear();
        agentListener.RecordObservableInstruments();
        poolListener.RecordObservableInstruments();

        agentReadings.Should().BeEmpty(
            "after a failed poll the agent gauges must emit nothing (_agentMetricsPolled reset to false)");
        poolReadings.Should().BeEmpty(
            "after a failed poll the credential-pool gauges must emit nothing (_credentialPoolUpdated reset to false)");
    }
}
