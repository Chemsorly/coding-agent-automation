using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Telemetry;

namespace CodingAgentWebUI.UnitTests.Telemetry;

/// <summary>
/// Unit tests for <see cref="WorkDistributionTelemetry.RecordDispatchLatency"/>.
/// Verifies the shared method's contract: correct timestamp selection, null-coalescing of
/// AgentSelector, and that both histograms are recorded.
/// </summary>
[Collection("Metrics")]
[Trait("Feature", "DispatchLatencyMetrics")]
public sealed class WorkDistributionTelemetryDispatchLatencyTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string InstrumentName, double Value, string? TagValue)> _recordings = [];

    public WorkDistributionTelemetryDispatchLatencyTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            string? agentSelectorTag = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "agent_selector")
                {
                    agentSelectorTag = tag.Value?.ToString();
                    break;
                }
            }
            _recordings.Add((instrument.Name, measurement, agentSelectorTag));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    // TODO [WARNING]: The MeterListener captures recordings from the shared static WorkDistributionTelemetry
    // meter (a process-wide singleton). Tests in other collections that call RecordDispatchLatency or record
    // to the same meter can inject entries into _recordings. [Collection("Metrics")] serializes tests within
    // this class only. The .Contain(...) assertions tolerate phantom entries, but this structural fragility
    // could mask double-recording bugs. Consider isolating the meter per test instance (e.g., a dedicated
    // test Meter) if false-negative risk increases as the test suite grows. (review-findings.md line 17)

    [Fact]
    public void RecordDispatchLatency_UsesOriginalEnqueuedAt_WhenPresent()
    {
        // Arrange: OriginalEnqueuedAt is 60s ago; CreatedAt is only 10s ago
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now;
        var originalEnqueuedAt = now.AddSeconds(-60);
        var createdAt = now.AddSeconds(-10);

        // Act
        WorkDistributionTelemetry.RecordDispatchLatency(dispatchedAt, originalEnqueuedAt, createdAt, "dotnet");

        // Assert: latency should use OriginalEnqueuedAt (~60s), not CreatedAt (~10s)
        var dispatchLatencies = _recordings
            .Where(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds")
            .Select(r => r.Value)
            .ToList();
        // TODO [WARNING]: Lower-bound-only assertion (`>= 55.0`) does not rule out absurdly large values
        // from a buggy implementation (e.g., year-scale latency from UtcNow - epoch). Consider adding an
        // upper bound (e.g., `v < 70.0`) or switching to fixed past timestamps like
        // RecordDispatchLatency_UsesExplicitDispatchedAt to make the assertion falsifiable from both
        // directions. (review-findings.md line 63)
        dispatchLatencies.Should().Contain(v => v >= 55.0,
            "latency should reflect OriginalEnqueuedAt (60s ago), not CreatedAt (10s ago)");

        var pendingDurations = _recordings
            .Where(r => r.InstrumentName == "workdistribution.workitems_pending_duration_seconds")
            .Select(r => r.Value)
            .ToList();
        pendingDurations.Should().Contain(v => v >= 55.0,
            "pending duration should reflect OriginalEnqueuedAt (60s ago), not CreatedAt (10s ago)");
    }

    [Fact]
    public void RecordDispatchLatency_FallsBackToCreatedAt_WhenOriginalEnqueuedAtIsNull()
    {
        // Arrange: OriginalEnqueuedAt is null; CreatedAt is 15s ago
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now;
        var createdAt = now.AddSeconds(-15);

        // Act
        WorkDistributionTelemetry.RecordDispatchLatency(dispatchedAt, originalEnqueuedAt: null, createdAt, "dotnet");

        // Assert: latency should fall back to CreatedAt (~15s)
        var dispatchLatencies = _recordings
            .Where(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds")
            .Select(r => r.Value)
            .ToList();
        // TODO [WARNING]: The assertion window `>= 10.0 && < 50.0` is fragile — a buggy implementation
        // that records 0 (null OriginalEnqueuedAt used as anchor) could fall outside the window and pass
        // accidentally; real-time clock skew could also push a correct value outside the bounds. Consider
        // switching to fixed past timestamps (as in RecordDispatchLatency_UsesExplicitDispatchedAt) to
        // make the anchor selection deterministic and the assertion exact. (review-findings.md line 93)
        dispatchLatencies.Should().Contain(v => v >= 10.0 && v < 50.0,
            "latency should fall back to CreatedAt (15s ago)");

        var pendingDurations = _recordings
            .Where(r => r.InstrumentName == "workdistribution.workitems_pending_duration_seconds")
            .Select(r => r.Value)
            .ToList();
        pendingDurations.Should().Contain(v => v >= 10.0 && v < 50.0,
            "pending duration should fall back to CreatedAt (15s ago)");
    }

    [Fact]
    public void RecordDispatchLatency_NullAgentSelector_RecordsEmptyStringTag()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        // Count the number of dispatch_latency_seconds entries with an empty tag BEFORE this
        // test's Act. _recordings is a shared ConcurrentBag that accumulates across all tests
        // in this class; using AllSatisfy on the full bag causes spurious failures when prior
        // tests recorded entries with non-empty agentSelector values.
        var emptyTagCountBefore = _recordings
            .Count(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds"
                        && r.TagValue == "");

        // Act
        WorkDistributionTelemetry.RecordDispatchLatency(now, null, now.AddSeconds(-5), agentSelector: null);

        // Assert: at least one new dispatch_latency_seconds entry with TagValue="" must have been added
        var emptyTagCountAfter = _recordings
            .Count(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds"
                        && r.TagValue == "");

        emptyTagCountAfter.Should().BeGreaterThan(emptyTagCountBefore,
            "RecordDispatchLatency with null agentSelector should add a dispatch_latency_seconds entry with empty-string tag");
    }

    [Fact]
    public void RecordDispatchLatency_RecordsBothHistograms()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;

        // Act
        WorkDistributionTelemetry.RecordDispatchLatency(now, null, now.AddSeconds(-10), "selector-a");

        // Assert: both histograms must be recorded
        _recordings.Should().Contain(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds",
            "DispatchLatency histogram must be recorded");
        _recordings.Should().Contain(r => r.InstrumentName == "workdistribution.workitems_pending_duration_seconds",
            "PendingDuration histogram must be recorded");
    }

    [Fact]
    public void RecordDispatchLatency_UsesExplicitDispatchedAt()
    {
        // Arrange: use a known fixed dispatchedAt far in the past so the expected latency
        // cannot be confused with a UtcNow-based computation
        var createdAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var dispatchedAt = createdAt.AddSeconds(30); // exactly 30s after createdAt
        var expectedLatency = 30.0;

        // Act
        WorkDistributionTelemetry.RecordDispatchLatency(dispatchedAt, originalEnqueuedAt: null, createdAt, "test");

        // Assert: recorded latency must equal exactly (dispatchedAt - createdAt) = 30s ± 0.1s
        // If the method called DateTimeOffset.UtcNow internally, the result would be ~year-long,
        // not 30s — this test would catch that regression.
        var latencyRecordings = _recordings
            .Where(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds")
            .Select(r => r.Value)
            .ToList();
        latencyRecordings.Should().Contain(v => Math.Abs(v - expectedLatency) < 0.1,
            $"recorded latency should be exactly {expectedLatency}s (dispatchedAt - createdAt), not a UtcNow-based value");

        var pendingRecordings = _recordings
            .Where(r => r.InstrumentName == "workdistribution.workitems_pending_duration_seconds")
            .Select(r => r.Value)
            .ToList();
        pendingRecordings.Should().Contain(v => Math.Abs(v - expectedLatency) < 0.1,
            $"pending duration should also be exactly {expectedLatency}s");
    }
}
