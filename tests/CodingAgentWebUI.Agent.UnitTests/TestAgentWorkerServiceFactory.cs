using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Shared factory for creating <see cref="AgentWorkerService"/> instances in tests.
/// Encapsulates the construction of <see cref="AgentConnectionLifecycle"/>,
/// <see cref="AgentJobSlotManager"/>, <see cref="ChatJobHandler"/>,
/// <see cref="ConsolidationJobHandler"/>, and the coordinator service.
/// </summary>
internal static class TestAgentWorkerServiceFactory
{
    /// <summary>
    /// Creates an <see cref="AgentWorkerService"/> with default mocks suitable for unit tests.
    /// Returns the service along with its slot manager, connection lifecycle, and chat handler for test manipulation.
    /// </summary>
    public static (AgentWorkerService Service, AgentJobSlotManager SlotManager, AgentConnectionLifecycle Lifecycle, ChatJobHandler ChatHandler)
        CreateWithComponents(
            IHostApplicationLifetime? hostLifetime = null,
            IJobCompletionReporter? completionReporter = null,
            KiroCliLib.Core.IKiroCliOrchestrator? orchestrator = null,
            Serilog.ILogger? logger = null,
            IHubConnectionManager? hubManager = null,
            IHubConnectionManagerFactory? hubManagerFactory = null)
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
            new AgentId("test-agent"),
            lifetime, mockLogger);

        var chatHandler = CreateChatJobHandler(
            lifecycle, slotManager, mockOrchestrator, lifetime, mockLogger,
            isOpenCodeProvider: (Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "")
                .Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase),
            isChatMode: string.Equals(
                Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true", StringComparison.OrdinalIgnoreCase));
        var consolidationHandler = CreateConsolidationJobHandler(lifecycle, slotManager, mockOrchestrator, mockLogger);

        var service = new AgentWorkerService(new AgentWorkerServiceDependencies(
            lifecycle, slotManager,
            chatHandler,
            consolidationHandler,
            CreateMockExecutor(mockOrchestrator),
            reporter,
            mockLogger));

        return (service, slotManager, lifecycle, chatHandler);
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

    /// <summary>
    /// Creates a standalone <see cref="ChatJobHandler"/> for direct unit testing.
    /// </summary>
    public static ChatJobHandler CreateChatJobHandler(
        AgentConnectionLifecycle connectionLifecycle,
        AgentJobSlotManager slotManager,
        KiroCliLib.Core.IKiroCliOrchestrator? orchestrator = null,
        IHostApplicationLifetime? hostLifetime = null,
        Serilog.ILogger? logger = null,
        Func<Task>? signalAgentReady = null,
        bool isOpenCodeProvider = false,
        bool isChatMode = false)
    {
        var mockLogger = logger ?? new Mock<Serilog.ILogger>().Object;
        var mockOrchestrator = orchestrator ?? new Mock<KiroCliLib.Core.IKiroCliOrchestrator>().Object;
        var lifetime = hostLifetime ?? Mock.Of<IHostApplicationLifetime>();
        return new ChatJobHandler(new ChatJobHandlerDependencies(
            connectionLifecycle,
            slotManager,
            mockOrchestrator,
            Mock.Of<IHttpClientFactory>(),
            lifetime,
            SignalAgentReady: signalAgentReady ?? (() => Task.CompletedTask),
            IsOpenCodeProvider: isOpenCodeProvider,
            IsChatMode: isChatMode,
            Logger: mockLogger));
    }

    /// <summary>
    /// Creates a standalone <see cref="ConsolidationJobHandler"/> for direct unit testing.
    /// </summary>
    public static ConsolidationJobHandler CreateConsolidationJobHandler(
        AgentConnectionLifecycle connectionLifecycle,
        AgentJobSlotManager slotManager,
        KiroCliLib.Core.IKiroCliOrchestrator? orchestrator = null,
        Serilog.ILogger? logger = null)
    {
        var mockLogger = logger ?? new Mock<Serilog.ILogger>().Object;
        var mockOrchestrator = orchestrator ?? new Mock<KiroCliLib.Core.IKiroCliOrchestrator>().Object;
        return new ConsolidationJobHandler(
            connectionLifecycle,
            slotManager,
            CreateMockConsolidationExecutor(mockOrchestrator),
            mockLogger);
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
        return new LocalPipelineExecutor(new LocalPipelineExecutorDependencies(
            orchestrator,
            mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            mockQualityGateValidator.Object,
            mockLogger.Object,
            AgentIdentity: new AgentId("test-agent")));
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
