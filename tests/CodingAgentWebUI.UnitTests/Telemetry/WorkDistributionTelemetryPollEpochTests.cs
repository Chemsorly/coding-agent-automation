using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.UnitTests.Telemetry;

/// <summary>
/// Tests for the <c>DispatcherLastPollEpoch</c> observable gauge and the
/// <see cref="WorkDistributionTelemetry.RecordLastPollEpoch"/> method.
///
/// Verifies:
///   (A) The gauge emits no measurement before <c>RecordLastPollEpoch</c> is called.
///   (B) The gauge emits a measurement after <c>RecordLastPollEpoch</c> is called.
///   (C) The emitted epoch value is strictly positive (guards against the former
///       torn-read bug where the export thread could observe epoch == 0 even though
///       the "recorded" flag was true).
///
/// Tests are named with A_/B_/C_ prefixes so xUnit's alphabetical ordering runs them
/// in isolation-dependency order. All three share [Collection("Metrics")] to prevent
/// MeterListener collisions with the other metric tests in this assembly.
/// </summary>
[Collection("Metrics")]
[Trait("Feature", "DispatcherLastPollEpoch")]
public sealed class WorkDistributionTelemetryPollEpochTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<Measurement<double>> _gaugeReadings = [];

    // Reflection handle for the backing field, obtained once per test instance.
    private static readonly FieldInfo PollEpochMillisField =
        typeof(WorkDistributionTelemetry)
            .GetField("_pollEpochMillis", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "_pollEpochMillis field not found on WorkDistributionTelemetry — was it renamed?");

    public WorkDistributionTelemetryPollEpochTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.dispatcher_last_poll_epoch_seconds")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "workdistribution.dispatcher_last_poll_epoch_seconds")
                _gaugeReadings.Add(new Measurement<double>(measurement, tags));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Resets <c>_pollEpochMillis</c> to 0 (the "not-yet-recorded" sentinel) via reflection,
    /// then triggers observable instruments and asserts no measurement is emitted.
    ///
    /// This test MUST run before B_ and C_ because those tests write a non-zero epoch to the
    /// static field. Within [Collection("Metrics")], xUnit serializes test classes; within this
    /// class, tests run alphabetically, so A_ is guaranteed to run first.
    /// </summary>
    [Fact]
    public void A_GaugeEmitsNoMeasurement_BeforeRecordLastPollEpoch()
    {
        // Arrange: reset the backing field so this test is independent of execution order
        PollEpochMillisField.SetValue(null, 0L);

        // Act: trigger observable instrument callbacks
        _listener.RecordObservableInstruments();

        // Assert: gauge must emit nothing when epoch has never been recorded
        _gaugeReadings.Should().BeEmpty(
            "the DispatcherLastPollEpoch gauge must not emit any measurement before " +
            "RecordLastPollEpoch is called — a spurious zero export would fire the DispatcherStalled alert at startup");
    }

    /// <summary>
    /// Calls <see cref="WorkDistributionTelemetry.RecordLastPollEpoch"/>, then triggers
    /// observable instruments and asserts exactly one measurement is emitted with a value
    /// within 5 seconds of the current epoch.
    /// </summary>
    [Fact]
    public void B_GaugeEmitsMeasurement_AfterRecordLastPollEpoch()
    {
        // Arrange
        var beforeCallEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Act
        WorkDistributionTelemetry.RecordLastPollEpoch();
        _listener.RecordObservableInstruments();

        var afterCallEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Assert: exactly one measurement emitted, value in [beforeCall, afterCall+5]
        _gaugeReadings.Should().HaveCount(1,
            "the gauge should emit exactly one measurement after RecordLastPollEpoch is called");

        var emittedEpoch = _gaugeReadings.Single().Value;
        emittedEpoch.Should().BeGreaterThanOrEqualTo(beforeCallEpochSeconds,
            "emitted epoch must be at or after the epoch recorded immediately before the call");
        emittedEpoch.Should().BeLessThanOrEqualTo(afterCallEpochSeconds + 5.0,
            "emitted epoch must not be far in the future — value should reflect the call time");
    }

    /// <summary>
    /// Verifies that the emitted epoch value is strictly positive after
    /// <see cref="WorkDistributionTelemetry.RecordLastPollEpoch"/> is called.
    ///
    /// This is the behavioral canary for the former torn-read bug: with the old two-field
    /// design, the export thread could observe <c>_pollEpochRecorded == true</c> while
    /// <c>_lastPollEpochSeconds</c> still held its zero initial value, causing the gauge to
    /// emit 0.0 and fire the DispatcherStalled alert. With the single <c>volatile long</c>
    /// field, a non-zero read guarantees a valid epoch millisecond value was stored.
    /// </summary>
    [Fact]
    public void C_GaugeEpochIsNonZero_AfterRecordLastPollEpoch()
    {
        // Act
        WorkDistributionTelemetry.RecordLastPollEpoch();
        _listener.RecordObservableInstruments();

        // Assert: all emitted values must be strictly positive
        _gaugeReadings.Should().NotBeEmpty(
            "RecordLastPollEpoch must cause the gauge to emit at least one measurement");
        _gaugeReadings.Should().AllSatisfy(m =>
            m.Value.Should().BeGreaterThan(0.0,
                "a zero epoch would incorrectly indicate the 'not recorded' state and trigger " +
                "the DispatcherStalled alert — this guards against the torn-read regression"));
    }
}
