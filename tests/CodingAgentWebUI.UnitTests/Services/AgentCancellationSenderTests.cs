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
        // TODO: "run-1" uses implicit string→RunId conversion. The new RunId-typed overload is exercised
        // by SendCancelJobAsync_WithRunId_PassesValueToAgentCommunication. Consider updating these
        // pre-existing tests to pass RunId literals directly so they don't depend on the implicit operator.
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
            Labels = []
        }, "conn-123");

        _mockComm
            .Setup(c => c.CancelJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Should not throw
        await _sender.SendCancelJobAsync("agent-1", "run-42");

        // Communication was attempted — the exception was caught, not silently skipped
        _mockComm.Verify(
            c => c.CancelJobAsync("conn-123", "run-42", It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task SendCancelJobAsync_WithRunId_PassesValueToAgentCommunication()
    {
        // Arrange
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-run-id-test",
            Hostname = "host-1",
            Labels = []
        }, "conn-runid");

        var runId = new RunId("run-value-42");

        // Act
        await _sender.SendCancelJobAsync("agent-run-id-test", runId);

        // Assert: the RunId.Value string is forwarded intact to IAgentCommunication.CancelJobAsync
        _mockComm.Verify(
            c => c.CancelJobAsync("conn-runid", "run-value-42", It.IsAny<CancellationToken>()),
            Times.Once,
            "RunId.Value must be forwarded as the string jobId to the wire-format CancelJobAsync");
    }

    // TODO: Add negative test — SendCancelJobAsync with empty-string AgentId should throw ArgumentException.
    // e.g., _sender.SendCancelJobAsync(new AgentId(""), "run-1") should throw.
    // TODO: Add negative test — SendCancelJobAsync with default(AgentId) (Value == null) should throw.
    // e.g., _sender.SendCancelJobAsync(default, "run-1") should throw ArgumentException.
    // TODO: Add negative test — SendCancelJobAsync with empty RunId.Value should throw ArgumentException.
    // e.g., _sender.SendCancelJobAsync("agent-1", new RunId("")) should throw. The guard
    // ArgumentException.ThrowIfNullOrEmpty(runId.Value) in AgentCancellationSender is tested
    // for ConsolidationDispatchService but not for AgentCancellationSender itself.
    // Also consider a test for default(RunId) (null .Value path) to cover both halves of ThrowIfNullOrEmpty.
}
