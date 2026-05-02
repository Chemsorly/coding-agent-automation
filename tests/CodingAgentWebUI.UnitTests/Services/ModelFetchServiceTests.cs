using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for ModelFetchService — model list delegation, caching, and error handling.
/// </summary>
public class ModelFetchServiceTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<IHubContext<AgentHub, IAgentHubClient>> _mockHubContext;
    private readonly Mock<IAgentHubClient> _mockClientProxy;
    private readonly Mock<ILogger> _mockLogger;
    private readonly ModelFetchService _service;

    public ModelFetchServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _mockHubContext = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        _mockClientProxy = new Mock<IAgentHubClient>();

        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _service = new ModelFetchService(_registry, _mockHubContext.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task FetchModelsAsync_NoAgents_ThrowsWithClearMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.FetchModelsAsync(CancellationToken.None));

        ex.Message.Should().Contain("No agents available");
    }

    [Fact]
    public async Task FetchModelsAsync_AllAgentsDisconnected_ThrowsWithClearMessage()
    {
        RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.FetchModelsAsync(CancellationToken.None));

        ex.Message.Should().Contain("No agents available");
    }

    [Fact]
    public async Task FetchModelsAsync_AgentResponds_ReturnsModels()
    {
        RegisterAgent("agent-1", "conn-1");

        // Simulate agent responding when RequestModelList is called
        _mockClientProxy
            .Setup(c => c.RequestModelList(It.IsAny<ModelListRequest>()))
            .Callback<ModelListRequest>(req =>
            {
                _service.CompleteRequest(new ModelListResponse
                {
                    RequestId = req.RequestId,
                    Models = new[] { new ModelInfo { ModelId = "claude-sonnet-4", Description = "Sonnet", RateMultiplier = 1.0 } }
                });
            })
            .Returns(Task.CompletedTask);

        var models = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("claude-sonnet-4");
    }

    [Fact]
    public async Task FetchModelsAsync_CachesResult_SecondCallDoesNotContactAgent()
    {
        RegisterAgent("agent-1", "conn-1");

        _mockClientProxy
            .Setup(c => c.RequestModelList(It.IsAny<ModelListRequest>()))
            .Callback<ModelListRequest>(req =>
            {
                _service.CompleteRequest(new ModelListResponse
                {
                    RequestId = req.RequestId,
                    Models = new[] { new ModelInfo { ModelId = "model-1" } }
                });
            })
            .Returns(Task.CompletedTask);

        await _service.FetchModelsAsync(CancellationToken.None);
        var models = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().HaveCount(1);
        _mockClientProxy.Verify(c => c.RequestModelList(It.IsAny<ModelListRequest>()), Times.Once);
    }

    [Fact]
    public async Task FetchModelsAsync_AgentReturnsError_ThrowsWithAgentMessage()
    {
        RegisterAgent("agent-1", "conn-1");

        _mockClientProxy
            .Setup(c => c.RequestModelList(It.IsAny<ModelListRequest>()))
            .Callback<ModelListRequest>(req =>
            {
                _service.CompleteRequest(new ModelListResponse
                {
                    RequestId = req.RequestId,
                    Error = "kiro-cli not found"
                });
            })
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.FetchModelsAsync(CancellationToken.None));

        ex.Message.Should().Contain("kiro-cli not found");
    }

    [Fact]
    public async Task FetchModelsAsync_Timeout_ThrowsOperationCanceled()
    {
        RegisterAgent("agent-1", "conn-1");

        // Agent never responds — will timeout
        _mockClientProxy
            .Setup(c => c.RequestModelList(It.IsAny<ModelListRequest>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.FetchModelsAsync(cts.Token));
    }

    [Fact]
    public async Task FetchModelsAsync_PrefersIdleAgent()
    {
        RegisterAgent("agent-busy", "conn-busy");
        _registry.TransitionStatus("agent-busy", AgentStatus.Busy);
        RegisterAgent("agent-idle", "conn-idle");

        string? targetConnectionId = null;
        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client(It.IsAny<string>()))
            .Callback<string>(connId => targetConnectionId = connId)
            .Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _mockClientProxy
            .Setup(c => c.RequestModelList(It.IsAny<ModelListRequest>()))
            .Callback<ModelListRequest>(req =>
            {
                _service.CompleteRequest(new ModelListResponse
                {
                    RequestId = req.RequestId,
                    Models = new[] { new ModelInfo { ModelId = "model-1" } }
                });
            })
            .Returns(Task.CompletedTask);

        await _service.FetchModelsAsync(CancellationToken.None);

        targetConnectionId.Should().Be("conn-idle");
    }

    [Fact]
    public void CompleteRequest_UnknownRequestId_DoesNotThrow()
    {
        var response = new ModelListResponse { RequestId = "unknown-id", Models = [] };
        _service.CompleteRequest(response);
        // Should not throw — just logs a warning
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ModelFetchService(null!, _mockHubContext.Object, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullHubContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ModelFetchService(_registry, null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ModelFetchService(_registry, _mockHubContext.Object, null!));
    }

    private void RegisterAgent(string agentId, string connectionId)
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "test-host",
            AgentType = "kiro-dotnet",
            Labels = new[] { "kiro", "dotnet" }
        }, connectionId);
    }
}
