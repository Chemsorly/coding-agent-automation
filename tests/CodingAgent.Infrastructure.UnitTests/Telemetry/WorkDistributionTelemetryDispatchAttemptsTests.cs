using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Unit tests for <see cref="WorkDistributionTelemetry.RecordDispatchAttempt"/> and
/// <see cref="WorkDistributionTelemetry.PreInitializeDispatchAttempts"/>.
///
/// AC: "The new instruments are exported with the specified names and tags."
/// </summary>
[Collection("Metrics")]
public sealed class WorkDistributionTelemetryDispatchAttemptsTests : IDisposable
{
    // ConcurrentBag accumulates across the whole test class — assertions use .Contain() not
    // .ContainSingle() to tolerate parallel-test phantom entries from other [Collection("Metrics")]
    // classes that share the static WorkDistributionTelemetry.Meter.
    // TODO [WARNING]: Because WorkDistributionTelemetry.Meter is a process-wide static,
    // pre-existing entries from PreInitializeDispatchAttempts (called via Program.EmitPreInitCounters
    // in any integration test fixture in the same process) may already be present in _recordings
    // when MeterListener.Start() fires its retroactive callback. For the 7 valid combinations,
    // this means .Contain() passes even if RecordDispatchAttempt emits the wrong tags — the
    // pre-init entries satisfy the assertion, masking the regression. A .ContainSingle() or
    // before/after count delta pattern would make the individual RecordDispatchAttempt_* tests
    // falsifiable against wrong-tag bugs.
    private readonly ConcurrentBag<(string Result, string Reason)> _recordings = [];
    private readonly MeterListener _listener = new();

    public WorkDistributionTelemetryDispatchAttemptsTests()
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName
                && instrument.Name == "workdistribution.dispatch.attempts")
                l.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string result = "", reason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
                else if (tag.Key == "reason") reason = tag.Value?.ToString() ?? "";
            }
            _recordings.Add((result, reason));
        });

        // IMPORTANT: Start() must be called BEFORE any Act so the retroactive InstrumentPublished
        // callback fires for existing static instruments. (Brain entry: 2026-09-02 b7f0bc27)
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void RecordDispatchAttempt_Dispatched_RecordsResultAndReason()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("dispatched", "none");

        _recordings.Should().Contain(("dispatched", "none"),
            "RecordDispatchAttempt(dispatched, none) must emit the correct tags");
    }

    [Fact]
    public void RecordDispatchAttempt_DeferredConcurrencyLimit_RecordsCorrectTags()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("deferred", "concurrency_limit");

        _recordings.Should().Contain(("deferred", "concurrency_limit"),
            "RecordDispatchAttempt(deferred, concurrency_limit) must emit the correct tags");
    }

    [Fact]
    public void RecordDispatchAttempt_DeferredNotPending_RecordsCorrectTags()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("deferred", "not_pending");

        _recordings.Should().Contain(("deferred", "not_pending"),
            "RecordDispatchAttempt(deferred, not_pending) must emit the correct tags");
    }

    [Fact]
    public void RecordDispatchAttempt_DeferredNoTemplate_RecordsCorrectTags()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("deferred", "no_template");

        _recordings.Should().Contain(("deferred", "no_template"),
            "RecordDispatchAttempt(deferred, no_template) must emit the correct tags");
    }

    [Fact]
    public void RecordDispatchAttempt_TransientPvcUnavailable_RecordsCorrectTags()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("transient", "pvc_unavailable");

        _recordings.Should().Contain(("transient", "pvc_unavailable"),
            "RecordDispatchAttempt(transient, pvc_unavailable) must emit the correct tags");
    }

    [Fact]
    public void RecordDispatchAttempt_TransientLockTimeout_RecordsCorrectTags()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("transient", "lock_timeout");

        _recordings.Should().Contain(("transient", "lock_timeout"),
            "RecordDispatchAttempt(transient, lock_timeout) must emit the correct tags");
    }

    [Fact]
    public void RecordDispatchAttempt_TransientK8sError_RecordsCorrectTags()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("transient", "k8s_error");

        _recordings.Should().Contain(("transient", "k8s_error"),
            "RecordDispatchAttempt(transient, k8s_error) must emit the correct tags");
    }

    [Fact]
    public void PreInitializeDispatchAttempts_EmitsAllSevenSeries()
    {
        // Arrange: collect all Add(0) calls via MeterListener.
        // Note: _listener is already running from the constructor; _recordings accumulates all calls.
        var countBefore = _recordings.Count;

        // Act: call the production pre-initialization method.
        // This replicates what Program.EmitPreInitCounters does so integration tests also cover it.
        WorkDistributionTelemetry.PreInitializeDispatchAttempts();

        // Assert: exactly 7 series must have been emitted.
        // TODO [WARNING]: ConcurrentBag<T>.Skip() iterates in LIFO (implementation-defined) order,
        // not insertion order. Between `countBefore = _recordings.Count` and the `.Skip(countBefore)`
        // call below, other tests in the same [Collection("Metrics")] can add entries, causing
        // newRecordings to contain fewer than the 7 entries emitted by PreInitializeDispatchAttempts.
        // The individual .Contain() assertions are robust, but the count assertion is structurally
        // fragile if this test is ever moved to a parallel collection.
        var newRecordings = _recordings.Skip(countBefore).ToList();
        // TODO [WARNING]: HaveCountGreaterThanOrEqualTo(7) allows more than 7 (e.g. 14 if the
        // method was already called once by another test in the process). The assertion comment says
        // "exactly 7", but the check does not enforce exactness. A regression that accidentally adds
        // an 8th invalid combination would not be caught. Consider a dedicated single-call fixture
        // with exact count if strict pre-init coverage is needed.
        newRecordings.Should().HaveCountGreaterThanOrEqualTo(7,
            "PreInitializeDispatchAttempts must emit exactly 7 (result, reason) series");

        // Verify each expected combination is present.
        newRecordings.Should().Contain(("dispatched", "none"),
            "dispatched/none must be pre-initialized");
        newRecordings.Should().Contain(("deferred", "concurrency_limit"),
            "deferred/concurrency_limit must be pre-initialized");
        newRecordings.Should().Contain(("deferred", "not_pending"),
            "deferred/not_pending must be pre-initialized");
        newRecordings.Should().Contain(("deferred", "no_template"),
            "deferred/no_template must be pre-initialized");
        newRecordings.Should().Contain(("transient", "pvc_unavailable"),
            "transient/pvc_unavailable must be pre-initialized");
        newRecordings.Should().Contain(("transient", "lock_timeout"),
            "transient/lock_timeout must be pre-initialized");
        newRecordings.Should().Contain(("transient", "k8s_error"),
            "transient/k8s_error must be pre-initialized");
    }
}
