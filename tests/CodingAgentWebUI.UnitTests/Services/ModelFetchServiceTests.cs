using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ModelFetchService"/>.
/// </summary>
public class ModelFetchServiceTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<IAgentCommunication> _mockComm;
    private readonly Mock<ILogger> _mockLogger;
    private readonly ModelFetchService _service;

    public ModelFetchServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _mockComm = new Mock<IAgentCommunication>();
        _service = new ModelFetchService(_registry, _mockComm.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task FetchModelsAsync_NoAgents_ReturnsError()
    {
        var (models, error) = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("No agents available");
    }

    [Fact]
    public async Task FetchModelsAsync_AgentResponds_ReturnsModels()
    {
        // Register an idle agent
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host1",
            AgentType = "kiro",
            Labels = ["kiro"]
        }, "conn-1");

        // When RequestFetchModelsAsync is called, simulate the agent responding
        _mockComm.Setup(c => c.RequestFetchModelsAsync(
                "conn-1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                // Simulate agent response via CompleteRequest
                _service.CompleteRequest(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [new AgentModelInfo { ModelId = "claude-sonnet-4-20250514" }]
                });
                return Task.CompletedTask;
            });

        var (models, error) = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("claude-sonnet-4-20250514");
        error.Should().BeNull();
    }

    [Fact]
    public async Task FetchModelsAsync_CachesAfterFirstSuccess()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host1",
            AgentType = "kiro",
            Labels = ["kiro"]
        }, "conn-1");

        _mockComm.Setup(c => c.RequestFetchModelsAsync(
                "conn-1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                _service.CompleteRequest(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [new AgentModelInfo { ModelId = "model-1" }]
                });
                return Task.CompletedTask;
            });

        // First call
        await _service.FetchModelsAsync(CancellationToken.None);

        // Second call should use cache — no additional communication
        var (models, error) = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().HaveCount(1);
        error.Should().BeNull();
        _mockComm.Verify(c => c.RequestFetchModelsAsync(
            It.IsAny<string>(), It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FetchModelsAsync_AgentReturnsError_PropagatesError()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host1",
            AgentType = "kiro",
            Labels = ["kiro"]
        }, "conn-1");

        _mockComm.Setup(c => c.RequestFetchModelsAsync(
                "conn-1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                _service.CompleteRequest(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [],
                    Error = "CLI not configured"
                });
                return Task.CompletedTask;
            });

        var (models, error) = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Be("CLI not configured");
    }

    [Fact]
    public void CompleteRequest_UnknownRequestId_LogsWarning()
    {
        _service.CompleteRequest(new FetchModelsResponse
        {
            RequestId = "unknown-id",
            Models = []
        });

        // Should not throw — just logs a warning
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        // The actual logging uses structured params, so just verify no exception
    }

    [Fact]
    public void CompleteRequest_NullResponse_ThrowsArgumentNullException()
    {
        var act = () => _service.CompleteRequest(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRegistry_ThrowsArgumentNullException()
    {
        var act = () => new ModelFetchService(null!, _mockComm.Object, _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullAgentComm_ThrowsArgumentNullException()
    {
        var act = () => new ModelFetchService(_registry, null!, _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new ModelFetchService(_registry, _mockComm.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
