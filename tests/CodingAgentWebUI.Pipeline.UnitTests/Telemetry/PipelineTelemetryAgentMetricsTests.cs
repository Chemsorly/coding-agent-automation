using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests verifying agent worker metric instruments emit correct tags.
/// </summary>
public class PipelineTelemetryAgentMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string InstrumentName, List<KeyValuePair<string, object?>> Tags)> _measurements = [];

    public PipelineTelemetryAgentMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            var tagList = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags)
                tagList.Add(tag);
            _measurements.Add((instrument.Name, tagList));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void AgentJobsReceived_Add_EmitsCounter()
    {
        _measurements.Clear();

        PipelineTelemetry.AgentJobsReceived.Add(1);

        _measurements.Should().Contain(m => m.InstrumentName == "agent.jobs.received");
    }

    [Fact]
    public void AgentJobsRejected_Add_IncludesReasonTag()
    {
        _measurements.Clear();

        PipelineTelemetry.AgentJobsRejected.Add(1,
            new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));

        var entries = _measurements.Where(m => m.InstrumentName == "agent.jobs.rejected").ToList();
        entries.Should().NotBeEmpty();
        entries.Should().Contain(e =>
            e.Tags.Contains(new KeyValuePair<string, object?>("reason", "busy")));
    }

    [Fact]
    public void AgentHeartbeatFailures_Add_EmitsCounter()
    {
        _measurements.Clear();

        PipelineTelemetry.AgentHeartbeatFailures.Add(1);

        _measurements.Should().Contain(m => m.InstrumentName == "agent.heartbeat.failures");
    }

    [Fact]
    public void AgentReconnections_Add_EmitsCounter()
    {
        _measurements.Clear();

        PipelineTelemetry.AgentReconnections.Add(1);

        _measurements.Should().Contain(m => m.InstrumentName == "agent.reconnections");
    }

    [Fact]
    public void AgentRejectionReasons_HasExpectedConstants()
    {
        PipelineTelemetry.AgentRejectionReasons.Busy.Should().Be("busy");
        PipelineTelemetry.AgentRejectionReasons.ShuttingDown.Should().Be("shutting_down");
        PipelineTelemetry.AgentRejectionReasons.Unknown.Should().Be("unknown");
    }
}
