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





    // ── Behavioral tests: mock IAgentService ─────────────────────────────

    [Fact]
    public void MockAgentService_CancelCurrentJob_CanBeInvoked()
    {
        var mock = new Mock<IAgentService>();

        mock.Object.CancelCurrentJob();

        mock.Verify(x => x.CancelCurrentJob(), Times.Once);
    }

    // ── Health endpoint source-code assertion ─────────────────────────────


    // ── Default property values on both services ─────────────────────────

    [Fact]
    public void AgentWorkerService_IsBusy_DefaultsFalse()
    {
        var service = CreateAgentWorkerService();

        ((IAgentService)service).IsBusy.Should().BeFalse();
    }

    [Fact]
    public void AgentWorkerService_CurrentStep_DefaultsNull()
    {
        var service = CreateAgentWorkerService();

        ((IAgentService)service).CurrentStep.Should().BeNull();
    }

    [Fact]
    public void WorkItemAgentService_IsBusy_DefaultsFalse()
    {
        var service = CreateWorkItemAgentService();

        ((IAgentService)service).IsBusy.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentWorkerService CreateAgentWorkerService()
    {
        return TestAgentWorkerServiceFactory.Create();
    }

    private static WorkItemAgentService CreateWorkItemAgentService()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockExecutor = new Mock<IWorkItemExecutor>();
        var mockLifecycleClient = new Mock<IWorkItemLifecycleClient>();

        return new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "test-work-item-id",
            mockLifecycleClient.Object,
            Mock.Of<IAgentConnectionManager>(),
            mockExecutor.Object,
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("test-agent"),
            Mock.Of<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            mockLogger.Object));
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
