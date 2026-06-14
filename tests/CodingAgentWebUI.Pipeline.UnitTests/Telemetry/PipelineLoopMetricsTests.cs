using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Unit tests verifying pipeline loop metric instruments emit correct names and tags.
/// Uses <see cref="MeterListener"/> to capture emissions from the static <see cref="PipelineTelemetry.Meter"/>.
/// </summary>
public class PipelineLoopMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];

    public PipelineLoopMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _counters.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void LoopPolls_EmitsWithResultSuccess()
    {
        PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", "success"));

        _counters.Should().Contain(c => c.Name == "pipeline.loop.polls"
            && c.Tags.Contains(new KeyValuePair<string, object?>("result", "success")));
    }

    [Fact]
    public void LoopPolls_EmitsWithResultFailure()
    {
        PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", "failure"));

        _counters.Should().Contain(c => c.Name == "pipeline.loop.polls"
            && c.Tags.Contains(new KeyValuePair<string, object?>("result", "failure")));
    }

    [Fact]
    public void LoopPolls_EmitsWithResultPartialFailure()
    {
        PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", "partial_failure"));

        _counters.Should().Contain(c => c.Name == "pipeline.loop.polls"
            && c.Tags.Contains(new KeyValuePair<string, object?>("result", "partial_failure")));
    }

    [Fact]
    public void LoopIssuesFound_EmitsWithCorrectCount()
    {
        PipelineTelemetry.LoopIssuesFound.Add(7);

        _counters.Should().Contain(c => c.Name == "pipeline.loop.issues_found" && c.Value == 7);
    }

    [Theory]
    [InlineData(PipelineTelemetry.LoopDecisions.Dispatched)]
    [InlineData(PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing)]
    [InlineData(PipelineTelemetry.LoopDecisions.SkippedDependencyBlocked)]
    [InlineData(PipelineTelemetry.LoopDecisions.SkippedNoAgent)]
    [InlineData(PipelineTelemetry.LoopDecisions.SkippedMaxRuns)]
    [InlineData(PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel)]
    public void LoopDispatchDecisions_EmitsWithDecisionTag(string decision)
    {
        PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", decision));

        _counters.Should().Contain(c => c.Name == "pipeline.loop.dispatch_decisions"
            && c.Tags.Contains(new KeyValuePair<string, object?>("decision", decision)));
    }

    [Fact]
    public void LoopBackoffEvents_Emits()
    {
        PipelineTelemetry.LoopBackoffEvents.Add(1);

        _counters.Should().Contain(c => c.Name == "pipeline.loop.backoff_events" && c.Value == 1);
    }

    [Fact]
    public void LoopCircuitBreakerTrips_Emits()
    {
        PipelineTelemetry.LoopCircuitBreakerTrips.Add(1);

        _counters.Should().Contain(c => c.Name == "pipeline.loop.circuit_breaker_trips" && c.Value == 1);
    }

    [Fact]
    public void LoopDecisions_ConstantsAreStable()
    {
        PipelineTelemetry.LoopDecisions.Dispatched.Should().Be("dispatched");
        PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing.Should().Be("skipped_already_processing");
        PipelineTelemetry.LoopDecisions.SkippedDependencyBlocked.Should().Be("skipped_dependency_blocked");
        PipelineTelemetry.LoopDecisions.SkippedNoAgent.Should().Be("skipped_no_agent");
        PipelineTelemetry.LoopDecisions.SkippedMaxRuns.Should().Be("skipped_max_runs");
        PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel.Should().Be("skipped_filtered_by_label");
    }
}
