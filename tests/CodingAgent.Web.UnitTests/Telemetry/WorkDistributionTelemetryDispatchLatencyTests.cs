using CodingAgent.Pipeline.Telemetry;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;

namespace CodingAgent.Web.UnitTests.Telemetry;

/// <summary>
/// Unit tests for <see cref="WorkDistributionTelemetry.RecordDispatchLatency"/>.
/// Verifies the shared method's contract: correct timestamp selection, null-coalescing of
/// AgentSelector, and that the dispatch_latency_seconds histogram is recorded.
/// Note: the duplicate workitems_pending_duration_seconds histogram was removed in issue #2976.
/// </summary>
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
        dispatchLatencies.Should().Contain(v => v >= 55.0,
            "latency should reflect OriginalEnqueuedAt (60s ago), not CreatedAt (10s ago)");
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
        dispatchLatencies.Should().Contain(v => v >= 10.0 && v < 50.0,
            "latency should fall back to CreatedAt (15s ago)");
    }

    [Fact]
    public void RecordDispatchLatency_NullAgentSelector_RecordsEmptyStringTag()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
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
    public void RecordDispatchLatency_RecordsDispatchLatencyHistogram()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;

        // Act
        WorkDistributionTelemetry.RecordDispatchLatency(now, null, now.AddSeconds(-10), "selector-a");

        // Assert: dispatch_latency_seconds must be recorded
        _recordings.Should().Contain(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds",
            "DispatchLatency histogram must be recorded");
        // Note: workitems_pending_duration_seconds was removed in issue #2976.
        _recordings.Should().NotContain(r => r.InstrumentName == "workdistribution.workitems_pending_duration_seconds",
            "PendingDuration histogram was removed in issue #2976 — it must not be emitted");
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
        var latencyRecordings = _recordings
            .Where(r => r.InstrumentName == "workdistribution.dispatch_latency_seconds")
            .Select(r => r.Value)
            .ToList();
        latencyRecordings.Should().Contain(v => Math.Abs(v - expectedLatency) < 0.1,
            $"recorded latency should be exactly {expectedLatency}s (dispatchedAt - createdAt), not a UtcNow-based value");
    }
}
