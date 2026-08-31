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
// TODO: [WARNING] xUnit does not guarantee alphabetical ordering of test methods within a class.
// The default order is reflection metadata order (typically source order), not alphabetical.
// The A_/B_/C_ prefix naming expresses intent but does not enforce ordering. Consider using
// [TestCaseOrderer] if the A_-first dependency is required. The current design is safe because
// xUnit creates a new class instance per test (fresh _gaugeReadings bag) and A_ resets the
// static field unconditionally, but the comment overstates the guarantee.
// TODO: [WARNING] The MeterListener subscribes to the static WorkDistributionTelemetry Meter at
// class load time via InstrumentPublished. If the static Meter instance is disposed by another
// test's teardown path (e.g. a test in [Collection("Metrics")] that calls Meter.Dispose()),
// the listener will silently receive no callbacks and the Should().NotBeEmpty() assertion in
// C_GaugeEpochIsNonZero_AfterRecordLastPollEpoch will fail with a misleading error. Consider
// adding a guard after _listener.Start() to verify the Meter is still live.
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
        // TODO: [WARNING] This reflection SetValue does not issue a memory barrier (no Volatile.Write).
        // On weakly-ordered architectures (ARM) or with aggressive JIT reordering, the 0L written here
        // may not be visible to the gauge lambda running on another thread when RecordObservableInstruments
        // is called synchronously below. In practice this passes on x86-64 (TSO model), but could
        // transiently fail on ARM or under heavy thread preemption. Consider resetting via a helper that
        // uses System.Threading.Volatile.Write, or expose a test-only reset API on WorkDistributionTelemetry.
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
        // TODO: [WARNING] beforeCallEpochSeconds uses ToUnixTimeSeconds() (integer seconds), while
        // emittedEpoch is _pollEpochMillis / 1000.0 (double). If the call fires within the same
        // integer second, the comparison is fine. To be more precise and avoid any fractional-second
        // rounding edge case, capture beforeCallEpochMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        // and compare emittedEpoch >= beforeCallEpochMillis / 1000.0.
        var beforeCallEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Act
        WorkDistributionTelemetry.RecordLastPollEpoch();
        _listener.RecordObservableInstruments();

        var afterCallEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Assert: exactly one measurement emitted, value in [beforeCall, afterCall+5]
        // TODO: [WARNING] _gaugeReadings is a ConcurrentBag instance field; each xUnit test instance
        // gets a fresh bag, so cross-test accumulation is not expected. However, if this test runs
        // after A_ and A_ somehow caused a measurement to be emitted (which it should not, given
        // the 0L reset), the bag would not be empty at this point. Adding an explicit _gaugeReadings
        // clear or using Count delta assertions would make the precondition explicit.
        _gaugeReadings.Should().HaveCount(1,
            "the gauge should emit exactly one measurement after RecordLastPollEpoch is called");

        var emittedEpoch = _gaugeReadings.Single().Value;
        emittedEpoch.Should().BeGreaterThanOrEqualTo(beforeCallEpochSeconds,
            "emitted epoch must be at or after the epoch recorded immediately before the call");
        // TODO: [WARNING] The +5.0 upper-bound tolerance silently permits an implementation that
        // records a timestamp up to 5 seconds in the future. Since RecordLastPollEpoch records
        // UtcNow at call time and afterCallEpochSeconds is captured immediately after, the emitted
        // epoch (ms / 1000.0) can never legitimately exceed afterCallEpochSeconds + 1 (one full
        // second of integer truncation). Consider tightening to afterCallEpochSeconds + 2.0.
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
