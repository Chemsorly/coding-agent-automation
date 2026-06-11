using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentCancellationSender"/>.
/// </summary>
public sealed class AgentCancellationSenderTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<IAgentCommunication> _mockComm;
    private readonly Mock<ILogger> _mockLogger;
    private readonly AgentCancellationSender _sender;

    public AgentCancellationSenderTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _mockComm = new Mock<IAgentCommunication>();
        _sender = new AgentCancellationSender(_registry, _mockComm.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task SendCancelJobAsync_AgentNotInRegistry_DoesNotSend()
    {
        await _sender.SendCancelJobAsync("unknown-agent", "run-1");

        _mockComm.Verify(
            c => c.CancelJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendCancelJobAsync_AgentRegistered_SendsCancelJob()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            AgentType = "kiro",
            Labels = []
        }, "conn-123");

        await _sender.SendCancelJobAsync("agent-1", "run-42");

        _mockComm.Verify(
            c => c.CancelJobAsync("conn-123", "run-42", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendCancelJobAsync_CommunicationThrows_DoesNotPropagate()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            AgentType = "kiro",
            Labels = []
        }, "conn-123");

        _mockComm
            .Setup(c => c.CancelJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Should not throw
        await _sender.SendCancelJobAsync("agent-1", "run-42");
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var act = () => new AgentCancellationSender(null!, _mockComm.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullAgentComm_Throws()
    {
        var act = () => new AgentCancellationSender(_registry, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new AgentCancellationSender(_registry, _mockComm.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
