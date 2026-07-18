using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Shared factory for creating <see cref="AgentWorkerService"/> instances in tests.
/// Encapsulates the construction of <see cref="AgentConnectionLifecycle"/>,
/// <see cref="AgentJobSlotManager"/>, and the coordinator service.
/// </summary>
internal static class TestAgentWorkerServiceFactory
{
    /// <summary>
    /// Creates an <see cref="AgentWorkerService"/> with default mocks suitable for unit tests.
    /// Returns the service along with its slot manager and connection lifecycle for test manipulation.
    /// </summary>
    public static (AgentWorkerService Service, AgentJobSlotManager SlotManager, AgentConnectionLifecycle Lifecycle)
        CreateWithComponents(
            IHostApplicationLifetime? hostLifetime = null,
            IJobCompletionReporter? completionReporter = null,
            KiroCliLib.Core.IKiroCliOrchestrator? orchestrator = null,
            Serilog.ILogger? logger = null,
            HubConnectionManager? hubManager = null,
            HubConnectionManagerFactory? hubManagerFactory = null)
    {
        var mockLogger = logger ?? new Mock<Serilog.ILogger>().Object;
        var mockOrchestrator = orchestrator ?? new Mock<KiroCliLib.Core.IKiroCliOrchestrator>().Object;
        var lifetime = hostLifetime ?? Mock.Of<IHostApplicationLifetime>();

        var hm = hubManager ?? CreateTestHubManager(mockLogger);
        var hmFactory = hubManagerFactory ?? CreateTestHubManagerFactory(mockLogger);

        var buffer = new CriticalMessageBuffer();
        var signalRPipeline = CodingAgentWebUI.Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(mockLogger);
        var signalRReporter = new SignalRCompletionReporter(hm, signalRPipeline, buffer, mockLogger);
        var reporter = completionReporter ?? signalRReporter;

        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        // TODO: signalReady callback is always a no-op in tests. This means tests never verify
        // that ReleaseJobSlotAndSignalReadyAsync actually invokes the callback. Use a mock/spy
        // that records invocations so tests can assert signalReady was called.
        var lifecycle = new AgentConnectionLifecycle(
            hm, hmFactory, signalRReporter, slotManager,
            new AgentIdentity("test-agent"),
            lifetime, mockLogger);

        var service = new AgentWorkerService(
            lifecycle, slotManager,
            new AgentIdentity("test-agent"),
            CreateMockExecutor(mockOrchestrator),
            CreateMockConsolidationExecutor(mockOrchestrator),
            reporter,
            mockOrchestrator,
            Mock.Of<IHttpClientFactory>(),
            mockLogger);

        return (service, slotManager, lifecycle);
    }

    /// <summary>
    /// Creates an <see cref="AgentWorkerService"/> with default mocks (simple usage).
    /// </summary>
    public static AgentWorkerService Create(
        IHostApplicationLifetime? hostLifetime = null,
        IJobCompletionReporter? completionReporter = null,
        KiroCliLib.Core.IKiroCliOrchestrator? orchestrator = null,
        Serilog.ILogger? logger = null)
    {
        return CreateWithComponents(hostLifetime, completionReporter, orchestrator, logger).Service;
    }

    public static HubConnectionManager CreateTestHubManager(Serilog.ILogger? logger = null)
    {
        var l = logger ?? new Mock<Serilog.ILogger>().Object;
        return new HubConnectionManager("http://localhost:9999", "test-agent", "test-api-key", l);
    }

    public static HubConnectionManagerFactory CreateTestHubManagerFactory(Serilog.ILogger? logger = null)
    {
        var l = logger ?? new Mock<Serilog.ILogger>().Object;
        return new HubConnectionManagerFactory("http://localhost:9999", "test-agent", "test-api-key", l);
    }

    private static LocalPipelineExecutor CreateMockExecutor(KiroCliLib.Core.IKiroCliOrchestrator orchestrator)
    {
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockQualityGateValidator = new Mock<IQualityGateValidator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalPipelineExecutor(
            orchestrator,
            mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            mockQualityGateValidator.Object,
            mockLogger.Object,
            agentIdentity: new AgentIdentity("test-agent"));
    }

    private static LocalConsolidationExecutor CreateMockConsolidationExecutor(KiroCliLib.Core.IKiroCliOrchestrator orchestrator)
    {
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalConsolidationExecutor(
            orchestrator,
            mockHttpClientFactory.Object,
            mockLogger.Object);
    }
}
