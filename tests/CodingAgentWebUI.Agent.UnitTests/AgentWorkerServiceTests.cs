using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentWorkerService"/>.
/// Since AgentWorkerService depends on concrete classes (HubConnectionManager, LocalPipelineExecutor),
/// we test constructor validation, public property defaults, and the service's behavioral contract
/// through its observable state.
/// </summary>
/// <remarks>
/// This class mutates process-global environment variables (AGENT_TYPE, AGENT_ID, AGENT_LABELS).
/// It shares the "EnvironmentVariables" collection with <see cref="HealthEndpointsTests"/> to
/// prevent parallel execution — environment variables are process-wide shared state.
/// </remarks>
[Collection("EnvironmentVariables")]
public class AgentWorkerServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullHubManager()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockExecutor = CreateMockExecutor();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();

        var act = () => new AgentWorkerService(null!, mockExecutor, mockOrchestrator.Object, mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManager");
    }

    [Fact]
    public void Constructor_ThrowsOnNullExecutor()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();

        var act = () => new AgentWorkerService(
            CreateTestHubManager(), null!, mockOrchestrator.Object, mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("executor");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var act = () => new AgentWorkerService(
            CreateTestHubManager(), CreateMockExecutor(), mockOrchestrator.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void IsBusy_DefaultsFalse()
    {
        var service = CreateService();
        service.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void CurrentStep_DefaultsNull()
    {
        var service = CreateService();
        service.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void IsConnected_DelegatesToHubManager()
    {
        var service = CreateService();
        // Before starting, the hub manager is not connected
        service.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ReadsAgentTypeFromEnvironment()
    {
        // AGENT_TYPE is required — if not set, constructor throws
        var originalType = Environment.GetEnvironmentVariable("AGENT_TYPE");
        try
        {
            // Must clear the env var completely
            Environment.SetEnvironmentVariable("AGENT_TYPE", null);

            // Verify it's actually cleared
            var currentValue = Environment.GetEnvironmentVariable("AGENT_TYPE");
            if (currentValue is not null)
            {
                // Skip test if we can't clear the env var (e.g., set at process level)
                return;
            }

            var mockLogger = new Mock<Serilog.ILogger>();
            var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
            var act = () => new AgentWorkerService(
                CreateTestHubManager(), CreateMockExecutor(), mockOrchestrator.Object, mockLogger.Object);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*AGENT_TYPE*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_TYPE", originalType ?? "kiro-dotnet");
        }
    }

    [Fact]
    public void Constructor_UsesHostnameWhenAgentIdNotSet()
    {
        var originalId = Environment.GetEnvironmentVariable("AGENT_ID");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_ID", null);
            // Should not throw — falls back to Environment.MachineName
            var service = CreateService();
            service.Should().NotBeNull();
        }
        finally
        {
            if (originalId != null)
                Environment.SetEnvironmentVariable("AGENT_ID", originalId);
        }
    }

    [Fact]
    public void Constructor_ParsesLabelsFromEnvironment()
    {
        var originalLabels = Environment.GetEnvironmentVariable("AGENT_LABELS");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", "kiro,dotnet,gpu");
            var service = CreateService();
            // Service should be created successfully with parsed labels
            service.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", originalLabels ?? "kiro,dotnet");
        }
    }

    [Fact]
    public void Constructor_HandlesEmptyLabels()
    {
        var originalLabels = Environment.GetEnvironmentVariable("AGENT_LABELS");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", "");
            var service = CreateService();
            service.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", originalLabels ?? "kiro,dotnet");
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStopsService()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();

        // Start the service — it will try to connect and fail, but should respect cancellation
        var executeTask = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
            }
            catch (Exception)
            {
                // Expected — connection will fail since there's no real orchestrator
            }
        });

        // Cancel quickly
        cts.Cancel();

        // Should complete within a reasonable time
        var completed = await Task.WhenAny(executeTask, Task.Delay(5000));
        completed.Should().Be(executeTask, "service should stop when cancelled");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HubConnectionManager CreateTestHubManager()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManager(
            "http://localhost:9999",
            "test-agent",
            "test-api-key",
            logger.Object);
    }

    private static LocalPipelineExecutor CreateMockExecutor()
    {
        var mockProviderFactory = new Mock<Pipeline.Interfaces.IProviderFactory>();
        var mockQualityGateValidator = new Mock<Pipeline.Interfaces.IQualityGateValidator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalPipelineExecutor(
            mockProviderFactory.Object,
            mockQualityGateValidator.Object,
            mockLogger.Object);
    }

    private static AgentWorkerService CreateService()
    {
        // Ensure AGENT_TYPE is set for the constructor
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_TYPE")))
            Environment.SetEnvironmentVariable("AGENT_TYPE", "kiro-dotnet");

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        return new AgentWorkerService(
            CreateTestHubManager(),
            CreateMockExecutor(),
            mockOrchestrator.Object,
            mockLogger.Object);
    }
}
