using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Tests for the owner-only guard on <c>workdistribution.credential_pool_available</c> and
/// <c>workdistribution.credential_pool_claimed</c> observable gauges.
///
/// Issue #2980: both gauges previously always emitted a measurement (even 0) from every process
/// that registered the meter. The fix adds an <c>_credentialPoolUpdated</c> sentinel — gauges
/// emit no measurement until <see cref="WorkDistributionTelemetry.UpdateCredentialPoolMetrics"/>
/// has been called, matching the pattern used by <c>DispatcherLastPollEpoch</c>.
///
/// Issue #3555 moved ownership from the API to the Scheduler leader. The sentinel mechanism is
/// unchanged — gauges still emit no measurement until <c>UpdateCredentialPoolMetrics</c> is
/// called (now by <c>WorkItemCountsService</c> on the Scheduler leader). A new
/// <c>ResetCredentialPoolMetrics</c> method resets the sentinel on poll failure.
///
/// Verifies:
///   (A) Gauges emit no measurement before <c>UpdateCredentialPoolMetrics</c> is called.
///   (B) Gauges emit correct measurements after <c>UpdateCredentialPoolMetrics</c> is called.
///
/// Tests are prefixed A_/B_ so xUnit's alphabetical ordering runs them in isolation-dependency
/// order. Both share [Collection("Metrics")] to prevent MeterListener collisions.
/// </summary>
[Collection("Metrics")]
[Trait("Feature", "CredentialPoolGaugeOwnerOnly")]
public sealed class WorkDistributionTelemetryCredentialPoolGaugeTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string InstrumentName, int Value)> _gaugeReadings = [];

    // Reflection handle to reset the backing sentinel field between test instances.
    private static readonly FieldInfo CredentialPoolUpdatedField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolUpdated", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_credentialPoolUpdated field not found on WorkDistributionTelemetry — was it renamed?");

    private static readonly FieldInfo CredentialPoolAvailableField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolAvailable", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_credentialPoolAvailable field not found on WorkDistributionTelemetry — was it renamed?");

    private static readonly FieldInfo CredentialPoolClaimedField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolClaimed", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_credentialPoolClaimed field not found on WorkDistributionTelemetry — was it renamed?");

    public WorkDistributionTelemetryCredentialPoolGaugeTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                (instrument.Name == "workdistribution.credential_pool_available" ||
                 instrument.Name == "workdistribution.credential_pool_claimed"))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
        {
            if (instrument.Name is "workdistribution.credential_pool_available"
                                 or "workdistribution.credential_pool_claimed")
                _gaugeReadings.Add((instrument.Name, measurement));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Before <see cref="WorkDistributionTelemetry.UpdateCredentialPoolMetrics"/> is called,
    /// both gauges must emit no measurement. This prevents Scheduler, Job Controller, and Web
    /// from emitting spurious 0 series that would pollute the CredentialPoolExhausted alert.
    /// </summary>
    [Fact]
    public void A_GaugesEmitNoMeasurement_BeforeUpdateCredentialPoolMetrics()
    {
        // TODO [WARNING]: The A_/B_ prefix convention relies on xUnit's alphabetical ordering, which
        // is not a guaranteed contract. If xUnit changes its default ordering or the tests are run
        // with a custom ordering plugin, B_ may execute before A_. The reflection reset in Arrange
        // mitigates this (the sentinel is reset to false before RecordObservableInstruments), so the
        // test logic itself is correct, but the isolation depends on the reflection reset being
        // fully complete before the OTel SDK fires the observable callback on the same thread.
        // RecordObservableInstruments() is synchronous, so this holds in practice. For long-term
        // robustness, consider using a factory-created meter per test instance (via TestMeterFactory)
        // rather than resetting static state via reflection.

        // Arrange: reset the sentinel so this test is independent of execution order
        CredentialPoolUpdatedField.SetValue(null, false);
        CredentialPoolAvailableField.SetValue(null, 0);
        CredentialPoolClaimedField.SetValue(null, 0);
        _gaugeReadings.Clear();

        // Act: trigger observable instrument callbacks
        _listener.RecordObservableInstruments();

        // Assert: no measurement must be emitted from either gauge
        _gaugeReadings.Should().BeEmpty(
            "credential pool gauges must not emit any measurement before " +
            "UpdateCredentialPoolMetrics is called — a spurious 0 export from non-API processes " +
            "would corrupt the CredentialPoolExhausted alert (issue #2980)");
    }

    /// <summary>
    /// After <see cref="WorkDistributionTelemetry.UpdateCredentialPoolMetrics"/> is called,
    /// both gauges must emit exactly one measurement each with the correct values and the
    /// <c>pool=kiro</c> tag.
    /// </summary>
    [Fact]
    public void B_GaugesEmitMeasurements_AfterUpdateCredentialPoolMetrics()
    {
        // Arrange: reset state then call UpdateCredentialPoolMetrics
        CredentialPoolUpdatedField.SetValue(null, false);
        CredentialPoolAvailableField.SetValue(null, 0);
        CredentialPoolClaimedField.SetValue(null, 0);
        _gaugeReadings.Clear();

        const int expectedAvailable = 3;
        const int expectedClaimed = 2;

        // Act
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(expectedAvailable, expectedClaimed);
        _listener.RecordObservableInstruments();

        // Assert: both gauges must have emitted exactly one measurement
        var availableReadings = _gaugeReadings
            .Where(r => r.InstrumentName == "workdistribution.credential_pool_available")
            .ToList();
        var claimedReadings = _gaugeReadings
            .Where(r => r.InstrumentName == "workdistribution.credential_pool_claimed")
            .ToList();

        availableReadings.Should().ContainSingle(
            "workdistribution.credential_pool_available must emit exactly one measurement after UpdateCredentialPoolMetrics");
        availableReadings.Single().Value.Should().Be(expectedAvailable,
            "available PVC count must match the value passed to UpdateCredentialPoolMetrics");
        // TODO [WARNING]: The pool=kiro tag is not asserted here because _gaugeReadings stores only
        // (InstrumentName, Value) tuples — tag data is discarded in the MeasurementEventCallback.
        // A regression that drops the "pool" tag from the production observeValues lambda would not
        // be caught. Fix: change _gaugeReadings to store tags alongside the value (e.g. use
        // ConcurrentBag<(string InstrumentName, int Value, ReadOnlySpan<KeyValuePair<string,object?>> Tags)>
        // or a plain List protected by a lock) and add tag assertions here.

        claimedReadings.Should().ContainSingle(
            "workdistribution.credential_pool_claimed must emit exactly one measurement after UpdateCredentialPoolMetrics");
        claimedReadings.Single().Value.Should().Be(expectedClaimed,
            "claimed PVC count must match the value passed to UpdateCredentialPoolMetrics");
        // TODO [WARNING]: Same tag-capture limitation applies to claimed — pool=kiro tag is not verified.
    }
}
