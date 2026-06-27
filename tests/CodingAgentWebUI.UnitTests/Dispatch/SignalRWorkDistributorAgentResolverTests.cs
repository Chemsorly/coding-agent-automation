using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="SignalRWorkDistributorAgentResolver"/>, specifically
/// the <see cref="ISignalRWorkDistributorAgentResolver.ReleaseLastResolvedAgent"/> method
/// that reverts agent Busy state after SignalR push failure.
/// </summary>
public class SignalRWorkDistributorAgentResolverTests
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly SignalRWorkDistributorAgentResolver _resolver;

    public SignalRWorkDistributorAgentResolverTests()
    {
        var logger = new Mock<ILogger>().Object;
        _registry = new AgentRegistryService(logger);
        _dispatcher = new JobDispatcherService(_registry, logger);
        _resolver = new SignalRWorkDistributorAgentResolver(_registry, _dispatcher);
    }

    [Fact]
    public void ResolveConnectionId_WithIdleAgent_ReturnsConnectionIdAndMarksAgentBusy()
    {
        // Arrange: register an idle agent
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Labels = ["dotnet"],
            Hostname = "host-1"
        }, "conn-abc");

        // Act
        var connectionId = _resolver.ResolveConnectionId("dotnet");

        // Assert
        connectionId.Should().Be("conn-abc");
        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public void ReleaseLastResolvedAgent_RevertsAgentToIdle()
    {
        // Arrange: register and resolve (marks Busy)
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-2",
            Labels = ["kiro"],
            Hostname = "host-2"
        }, "conn-def");
        _resolver.ResolveConnectionId("kiro");

        var agent = _registry.GetByAgentId("agent-2");
        agent!.Status.Should().Be(AgentStatus.Busy); // precondition

        // Act
        _resolver.ReleaseLastResolvedAgent();

        // Assert
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void ReleaseLastResolvedAgent_CalledWithoutResolve_DoesNotThrow()
    {
        // Act + Assert: no prior resolve, should be a no-op
        var act = () => _resolver.ReleaseLastResolvedAgent();
        act.Should().NotThrow();
    }

    [Fact]
    public void ReleaseLastResolvedAgent_CalledTwice_SecondCallIsNoOp()
    {
        // Arrange
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-3",
            Labels = ["python"],
            Hostname = "host-3"
        }, "conn-ghi");
        _resolver.ResolveConnectionId("python");

        // Act: release twice
        _resolver.ReleaseLastResolvedAgent();
        _resolver.ReleaseLastResolvedAgent(); // second call should be no-op

        // Assert: agent remains Idle (not double-transitioned)
        var agent = _registry.GetByAgentId("agent-3");
        agent!.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void ResolveConnectionId_NoMatchingAgent_ReturnsNull()
    {
        // Arrange: agent with different labels
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-4",
            Labels = ["java"],
            Hostname = "host-4"
        }, "conn-jkl");

        // Act
        var connectionId = _resolver.ResolveConnectionId("dotnet");

        // Assert
        connectionId.Should().BeNull();
    }
}
