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
/// Issue #3555: Both gauges are now emitted by the Scheduler leader via callbacks registered
/// with <see cref="WorkDistributionTelemetry.RegisterCredentialPoolGaugeCallbacks"/>.
/// The <c>UpdateCredentialPoolMetrics</c> method is now a no-op (obsolete).
///
/// Verifies:
///   (A) Gauges emit no measurement before <c>RegisterCredentialPoolGaugeCallbacks</c> is called.
///   (B) Gauges emit correct measurements after callbacks are registered and return data.
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

    // Reflection handles to reset the callback fields between test instances.
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
    /// Before <see cref="WorkDistributionTelemetry.RegisterCredentialPoolGaugeCallbacks"/> is called,
    /// both gauges must emit no measurement. This prevents API, Job Controller, and Web from
    /// emitting spurious 0 series that would pollute the CredentialPoolExhausted alert.
    /// </summary>
    [Fact]
    public void A_GaugesEmitNoMeasurement_BeforeRegisterCredentialPoolGaugeCallbacks()
    {
        // Arrange: reset the callbacks so this test is independent of execution order
        CredentialPoolAvailableCallbackField.SetValue(null, null);
        CredentialPoolClaimedCallbackField.SetValue(null, null);
        _gaugeReadings.Clear();

        // Act: trigger observable instrument callbacks
        _listener.RecordObservableInstruments();

        // Assert: no measurement must be emitted from either gauge
        _gaugeReadings.Should().BeEmpty(
            "credential pool gauges must not emit any measurement before " +
            "RegisterCredentialPoolGaugeCallbacks is called — a spurious 0 export from non-Scheduler processes " +
            "would corrupt the CredentialPoolExhausted alert (issue #3555)");
    }

    /// <summary>
    /// After <see cref="WorkDistributionTelemetry.RegisterCredentialPoolGaugeCallbacks"/> is called,
    /// both gauges must emit exactly one measurement each with the correct values.
    /// </summary>
    [Fact]
    public void B_GaugesEmitMeasurements_AfterRegisterCredentialPoolGaugeCallbacks()
    {
        // Arrange: reset callbacks first, then register new ones
        CredentialPoolAvailableCallbackField.SetValue(null, null);
        CredentialPoolClaimedCallbackField.SetValue(null, null);
        _gaugeReadings.Clear();

        const int expectedAvailable = 3;
        const int expectedClaimed = 2;

        // Act: register callbacks that return fixed measurements
        WorkDistributionTelemetry.RegisterCredentialPoolGaugeCallbacks(
            availableCallback: () => [new Measurement<int>(expectedAvailable,
                new KeyValuePair<string, object?>("pool", "kiro"))],
            claimedCallback: () => [new Measurement<int>(expectedClaimed,
                new KeyValuePair<string, object?>("pool", "kiro"))]);
        _listener.RecordObservableInstruments();

        // Assert: both gauges must have emitted exactly one measurement
        var availableReadings = _gaugeReadings
            .Where(r => r.InstrumentName == "workdistribution.credential_pool_available")
            .ToList();
        var claimedReadings = _gaugeReadings
            .Where(r => r.InstrumentName == "workdistribution.credential_pool_claimed")
            .ToList();

        availableReadings.Should().ContainSingle(
            "workdistribution.credential_pool_available must emit exactly one measurement after RegisterCredentialPoolGaugeCallbacks");
        availableReadings.Single().Value.Should().Be(expectedAvailable,
            "available PVC count must match the value returned by the registered callback");

        // TODO [WARNING]: The pool=kiro tag is not asserted here. _gaugeReadings stores only
        // (InstrumentName, Value) tuples — tag data is discarded in the MeasurementEventCallback.
        // A regression that drops or renames the "pool" tag from the production observeValues lambda
        // would not be caught. Fix: change _gaugeReadings to store tags alongside the value (e.g.
        // ConcurrentBag<(string InstrumentName, int Value, IEnumerable<KeyValuePair<string,object?>> Tags)>)
        // and add assertions on tag name and value here and for the claimed gauge below.

        claimedReadings.Should().ContainSingle(
            "workdistribution.credential_pool_claimed must emit exactly one measurement after RegisterCredentialPoolGaugeCallbacks");
        claimedReadings.Single().Value.Should().Be(expectedClaimed,
            "claimed PVC count must match the value returned by the registered callback");
    }
}
