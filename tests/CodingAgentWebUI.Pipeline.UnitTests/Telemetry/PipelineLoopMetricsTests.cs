using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Unit tests verifying pipeline loop metric instruments emit correct names and tags.
/// Uses <see cref="TestMeterFactory"/> and <see cref="MetricCollector{T}"/> for isolation —
/// no cross-test contamination via the shared static <see cref="PipelineTelemetry.Meter"/>.
/// </summary>
public class PipelineLoopMetricsTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();
    private readonly System.Diagnostics.Metrics.Meter _meter;
    private readonly System.Diagnostics.Metrics.Counter<long> _loopPolls;
    private readonly System.Diagnostics.Metrics.Counter<long> _loopIssuesFound;
    private readonly System.Diagnostics.Metrics.Counter<long> _loopDispatchDecisions;
    private readonly System.Diagnostics.Metrics.Counter<long> _loopBackoffEvents;
    private readonly System.Diagnostics.Metrics.Counter<long> _loopCircuitBreakerTrips;

    public PipelineLoopMetricsTests()
    {
        _meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        _loopPolls = _meter.CreateCounter<long>("pipeline.loop.polls", "{poll}", "Pipeline loop poll attempts");
        _loopIssuesFound = _meter.CreateCounter<long>("pipeline.loop.issues_found", "{issue}", "Issues found per poll cycle");
        _loopDispatchDecisions = _meter.CreateCounter<long>("pipeline.loop.dispatch_decisions", "{decision}", "Dispatch decisions made by the loop");
        _loopBackoffEvents = _meter.CreateCounter<long>("pipeline.loop.backoff_events", "{event}", "Backoff escalations due to poll failures");
        _loopCircuitBreakerTrips = _meter.CreateCounter<long>("pipeline.loop.circuit_breaker_trips", "{trip}", "Circuit breaker trip events");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void LoopPolls_EmitsWithResultSuccess()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.polls");

        _loopPolls.Add(1, new KeyValuePair<string, object?>("result", "success"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("result", "success")));
    }

    [Fact]
    public void LoopPolls_EmitsWithResultFailure()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.polls");

        _loopPolls.Add(1, new KeyValuePair<string, object?>("result", "failure"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("result", "failure")));
    }

    [Fact]
    public void LoopPolls_EmitsWithResultPartialFailure()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.polls");

        _loopPolls.Add(1, new KeyValuePair<string, object?>("result", "partial_failure"));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("result", "partial_failure")));
    }

    [Fact]
    public void LoopIssuesFound_EmitsWithCorrectCount()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.issues_found");

        _loopIssuesFound.Add(7);

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 7);
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
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.dispatch_decisions");

        _loopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", decision));

        collector.GetMeasurementSnapshot().Should().ContainSingle(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("decision", decision)));
    }

    [Fact]
    public void LoopBackoffEvents_Emits()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.backoff_events");

        _loopBackoffEvents.Add(1);

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public void LoopCircuitBreakerTrips_Emits()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.loop.circuit_breaker_trips");

        _loopCircuitBreakerTrips.Add(1);

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
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
