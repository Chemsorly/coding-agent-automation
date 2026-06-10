using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Telemetry;

public class QueueWaitTimeMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<double> _recordedWaitTimes = [];
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly JobQueueDrainService _drainService;
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;

    public QueueWaitTimeMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "dispatch.queue.wait_time")
                _recordedWaitTimes.Add(measurement);
        });

        _listener.Start();

        var logger = new Mock<ILogger>().Object;
        _registry = new AgentRegistryService(logger);
        _dispatcher = new JobDispatcherService(_registry, logger);
        _mockJobDispatcher = new Mock<IJobDispatcher>();

        _mockJobDispatcher.Setup(d => d.DispatchToAgentDirectAsync(
            It.IsAny<AgentEntry>(), It.IsAny<PendingJob>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigStore
            .Setup(c => c.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var consolidationQueue = new ConsolidationQueueService(logger);
        _drainService = new JobQueueDrainService(
            _dispatcher, _registry, _mockJobDispatcher.Object,
            mockConfigStore.Object, consolidationQueue, new Mock<IConsolidationService>().Object,
            new Mock<IConsolidationDispatcher>().Object, logger);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task DrainAsync_RecordsQueueWaitTime()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host", AgentType = "kiro",
            Labels = ["dotnet"]
        }, "conn-1");

        var job = new PendingJob
        {
            IssueIdentifier = "issue-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            InitiatedBy = "test",
            RequiredLabels = []
        };
        _dispatcher.EnqueueJob(job);

        await _drainService.DrainAsync(CancellationToken.None);

        // Use ContainSingle with predicate instead of HaveCount(1) because the static
        // MeterListener can pick up measurements from other tests running in parallel.
        _recordedWaitTimes.Should().Contain(t => t >= 5.0);
    }

    [Fact]
    public async Task DrainAsync_EmptyQueue_DoesNotRecordWaitTime()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host", AgentType = "kiro",
            Labels = ["dotnet"]
        }, "conn-1");

        // Capture count before drain to isolate from parallel test pollution via static Meter
        var countBefore = _recordedWaitTimes.Count;

        await _drainService.DrainAsync(CancellationToken.None);

        _recordedWaitTimes.Count.Should().Be(countBefore);
    }
}
