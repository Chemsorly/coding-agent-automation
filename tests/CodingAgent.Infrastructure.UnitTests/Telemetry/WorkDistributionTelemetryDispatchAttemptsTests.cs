using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Unit tests for <see cref="WorkDistributionTelemetry.RecordDispatchAttempt"/>.
///
/// AC: "The new instruments are exported with the specified names and tags."
/// </summary>
[Collection("Metrics")]
public sealed class WorkDistributionTelemetryDispatchAttemptsTests : IDisposable
{
    // ConcurrentBag accumulates across the whole test class — assertions use .Contain() not
    // .ContainSingle() to tolerate parallel-test phantom entries from other [Collection("Metrics")]
    // classes that share the static WorkDistributionTelemetry.Meter.
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
}
