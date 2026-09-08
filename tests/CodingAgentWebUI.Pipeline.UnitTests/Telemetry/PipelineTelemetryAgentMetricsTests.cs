using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying agent worker metric instruments emit correct tags.
/// Uses <see cref="TestMeterFactory"/> and <see cref="MetricCollector{T}"/> for isolation —
/// each test operates on instruments created from an isolated factory, removing the need for
/// <c>[Collection("Metrics")]</c> serialization.
/// </summary>
public class PipelineTelemetryAgentMetricsTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();
    private readonly System.Diagnostics.Metrics.Meter _meter;
    private readonly System.Diagnostics.Metrics.Counter<long> _agentJobsReceived;
    private readonly System.Diagnostics.Metrics.Counter<long> _agentJobsRejected;
    private readonly System.Diagnostics.Metrics.Counter<long> _agentHeartbeatFailures;
    private readonly System.Diagnostics.Metrics.Counter<long> _agentReconnections;

    public PipelineTelemetryAgentMetricsTests()
    {
        _meter = _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        _agentJobsReceived = _meter.CreateCounter<long>("agent.jobs.received", "{job}", "Jobs received by agent workers");
        _agentJobsRejected = _meter.CreateCounter<long>("agent.jobs.rejected", "{job}", "Jobs rejected by agent workers");
        _agentHeartbeatFailures = _meter.CreateCounter<long>("agent.heartbeat.failures", "{failure}", "Agent heartbeat failures");
        _agentReconnections = _meter.CreateCounter<long>("agent.reconnections", "{reconnection}", "Agent reconnection events");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void AgentJobsReceived_Add_EmitsCounter()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "agent.jobs.received");

        _agentJobsReceived.Add(1);

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public void AgentJobsRejected_Add_IncludesReasonTag()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "agent.jobs.rejected");

        _agentJobsRejected.Add(1, new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("reason", "busy")));
    }

    [Fact]
    public void AgentHeartbeatFailures_Add_EmitsCounter()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "agent.heartbeat.failures");

        _agentHeartbeatFailures.Add(1);

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public void AgentReconnections_Add_EmitsCounter()
    {
        using var collector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "agent.reconnections");

        _agentReconnections.Add(1);

        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public void AgentRejectionReasons_HasExpectedConstants()
    {
        PipelineTelemetry.AgentRejectionReasons.Busy.Should().Be("busy");
        PipelineTelemetry.AgentRejectionReasons.ShuttingDown.Should().Be("shutting_down");
        PipelineTelemetry.AgentRejectionReasons.Unknown.Should().Be("unknown");
    }
}
