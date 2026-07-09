using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="IAgentService"/> interface extraction (R4).
/// Defines the behavioral contract:
/// - Both AgentWorkerService and WorkItemAgentService implement IAgentService
/// - Health endpoints can query via IAgentService (not concrete type)
/// - Properties: IsBusy, CurrentStep, IsConnected
/// - Method: CancelCurrentJob()
/// </summary>
public class IAgentServiceTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void AgentWorkerService_Implements_IAgentService()
    {
        var service = CreateAgentWorkerService();
        service.Should().BeAssignableTo<IAgentService>();
    }

    [Fact]
    public void WorkItemAgentService_Implements_IAgentService()
    {
        var service = CreateWorkItemAgentService();
        service.Should().BeAssignableTo<IAgentService>();
    }

    // ── Interface definition ─────────────────────────────────────────────

    [Fact]
    public void IAgentService_HasIsBusyProperty()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IAgentService.cs"));

        sourceCode.Should().Contain("bool IsBusy",
            "IAgentService must define IsBusy property");
    }

    [Fact]
    public void IAgentService_HasCurrentStepProperty()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IAgentService.cs"));

        sourceCode.Should().Contain("PipelineStep?",
            "IAgentService must define CurrentStep property (nullable PipelineStep)");
    }

    [Fact]
    public void IAgentService_HasIsConnectedProperty()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IAgentService.cs"));

        sourceCode.Should().Contain("bool IsConnected",
            "IAgentService must define IsConnected property");
    }

    [Fact]
    public void IAgentService_HasCancelCurrentJobMethod()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IAgentService.cs"));

        sourceCode.Should().Contain("CancelCurrentJob",
            "IAgentService must define CancelCurrentJob method");
    }

    // ── Behavioral tests: mock IAgentService ─────────────────────────────

    [Fact]
    public void MockAgentService_IsBusy_ReturnsFalse_WhenIdle()
    {
        var mock = new Mock<IAgentService>();
        mock.Setup(x => x.IsBusy).Returns(false);

        mock.Object.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void MockAgentService_IsBusy_ReturnsTrue_WhenExecuting()
    {
        var mock = new Mock<IAgentService>();
        mock.Setup(x => x.IsBusy).Returns(true);

        mock.Object.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void MockAgentService_CurrentStep_ReturnsNull_WhenIdle()
    {
        var mock = new Mock<IAgentService>();
        mock.Setup(x => x.CurrentStep).Returns((PipelineStep?)null);

        mock.Object.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void MockAgentService_CurrentStep_ReturnsStep_WhenExecuting()
    {
        var mock = new Mock<IAgentService>();
        mock.Setup(x => x.CurrentStep).Returns(PipelineStep.GeneratingCode);

        mock.Object.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void MockAgentService_IsConnected_ReportsConnectionState()
    {
        var mock = new Mock<IAgentService>();
        mock.Setup(x => x.IsConnected).Returns(true);

        mock.Object.IsConnected.Should().BeTrue();
    }

    [Fact]
    public void MockAgentService_CancelCurrentJob_CanBeInvoked()
    {
        var mock = new Mock<IAgentService>();

        mock.Object.CancelCurrentJob();

        mock.Verify(x => x.CancelCurrentJob(), Times.Once);
    }

    // ── Health endpoint source-code assertion ─────────────────────────────

    [Fact]
    public void SourceCode_HealthEndpoints_UsesIAgentService()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "HealthEndpoints.cs"));

        sourceCode.Should().Contain("IAgentService",
            "HealthEndpoints readyz probe should query via IAgentService interface, not concrete AgentWorkerService");
    }

    // ── Default property values on both services ─────────────────────────

    [Fact]
    public void AgentWorkerService_IsBusy_DefaultsFalse()
    {
        var service = CreateAgentWorkerService();
        var agentService = (IAgentService)service;

        agentService.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void AgentWorkerService_CurrentStep_DefaultsNull()
    {
        var service = CreateAgentWorkerService();
        var agentService = (IAgentService)service;

        agentService.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void WorkItemAgentService_IsBusy_DefaultsFalse()
    {
        var service = CreateWorkItemAgentService();
        var agentService = (IAgentService)service;

        agentService.IsBusy.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentWorkerService CreateAgentWorkerService()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockPipeline = new Mock<IPipelineExecutor>();
        var mockConsolidation = new Mock<IConsolidationExecutor>();
        var hubManagerFactory = new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent", "test-key", mockLogger.Object);
        var hubManager = hubManagerFactory.Create();

        return new AgentWorkerService(
            hubManager,
            hubManagerFactory,
            mockPipeline.Object,
            mockConsolidation.Object,
            Mock.Of<IJobCompletionReporter>(),
            mockOrchestrator.Object,
            Mock.Of<IHttpClientFactory>(),
            new AgentIdentity("test-agent"),
            Mock.Of<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            mockLogger.Object);
    }

    private static WorkItemAgentService CreateWorkItemAgentService()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockExecutor = new Mock<IWorkItemExecutor>();
        var mockLifecycleClient = new Mock<IWorkItemLifecycleClient>();

        return new WorkItemAgentService(
            "test-work-item-id",
            mockLifecycleClient.Object,
            Mock.Of<IAgentConnectionManager>(),
            mockExecutor.Object,
            Mock.Of<IJobCompletionReporter>(),
            new AgentIdentity("test-agent"),
            Mock.Of<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            mockLogger.Object);
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
