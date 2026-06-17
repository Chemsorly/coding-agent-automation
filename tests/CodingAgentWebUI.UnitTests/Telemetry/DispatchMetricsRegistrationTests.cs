using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Telemetry;

public class DispatchMetricsRegistrationTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, int> _gaugeValues = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;

    public DispatchMetricsRegistrationTests()
    {
        var logger = new Mock<ILogger>().Object;
        _registry = new AgentRegistryService(logger);
        _dispatcher = new JobDispatcherService(_registry, logger);

        // Register gauges (same pattern as Program.cs)
        _ = PipelineTelemetry.Meter.CreateObservableGauge("dispatch.queue.depth",
            () => _dispatcher.QueueLength, "{item}", "Jobs waiting for available agent");
        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
            () => _registry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
            () => _registry.GetAllAgents().Count, "{connection}", "Total registered agents");

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
        {
            _gaugeValues[instrument.Name] = measurement;
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void QueueDepth_ReflectsQueueLength()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-1", IssueProviderId = "ip",
            RepoProviderId = "rp", EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test", RequiredLabels = []
        });

        _listener.RecordObservableInstruments();

        _gaugeValues.Should().ContainKey("dispatch.queue.depth");
        _gaugeValues["dispatch.queue.depth"].Should().Be(1);
    }

    [Fact]
    public void AgentJobsActive_ReflectsBusyAgentCount()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host", Labels = ["dotnet"]
        }, "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        _listener.RecordObservableInstruments();

        _gaugeValues.Should().ContainKey("agent.jobs.active");
        _gaugeValues["agent.jobs.active"].Should().Be(1);
    }

    [Fact]
    public void AgentConnectionsTotal_ReflectsRegisteredAgentCount()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host", Labels = ["dotnet"]
        }, "conn-1");
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-2", Hostname = "host", Labels = ["python"]
        }, "conn-2");

        _listener.RecordObservableInstruments();

        _gaugeValues.Should().ContainKey("agent.connections.total");
        _gaugeValues["agent.connections.total"].Should().Be(2);
    }
}
