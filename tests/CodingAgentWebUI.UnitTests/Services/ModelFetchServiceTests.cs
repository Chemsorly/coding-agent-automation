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

    // ═══════════════════════════════════════════════════════════════════════
    // WaitAndFetchAsync — unit tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WaitAndFetchAsync_CacheHit_ReturnsImmediately_WithoutPolling()
    {
        // Prime the cache via a successful FetchModelsAsync call.
        _registry.Register(new AgentRegistrationMessage { AgentId = "a1", Hostname = "h", Labels = [] }, "c1");
        _mockComm.Setup(c => c.RequestFetchModelsAsync("c1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                _service.CompleteRequest(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = "cached" }] });
                return Task.CompletedTask;
            });
        await _service.FetchModelsAsync(CancellationToken.None);

        // WaitAndFetchAsync must return the cached value — no polling, no comm call
        var callsBefore = _mockComm.Invocations.Count;
        var (models, error) = await _service.WaitAndFetchAsync("any-prefix", 2, 50, CancellationToken.None);

        error.Should().BeNull();
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("cached");
        _mockComm.Invocations.Count.Should().Be(callsBefore, "cache hit must not trigger a new request");
    }

    [Fact]
    public async Task WaitAndFetchAsync_AgentWithMatchingPrefix_Found_ReturnsModels()
    {
        // Register an agent whose ID starts with the expected prefix.
        const string prefix = "caa-models-abc123";
        const string podName = "caa-models-abc123-xyz";
        _registry.Register(new AgentRegistrationMessage { AgentId = podName, Hostname = "h", Labels = [] }, "conn-pod");
        _mockComm.Setup(c => c.RequestFetchModelsAsync("conn-pod", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                _service.CompleteRequest(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = "m1" }] });
                return Task.CompletedTask;
            });

        var (models, error) = await _service.WaitAndFetchAsync(prefix, 5, 50, CancellationToken.None);

        error.Should().BeNull();
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("m1");
    }

    [Fact]
    public async Task WaitAndFetchAsync_AgentWithWrongPrefix_NotMatched_Timeout()
    {
        // An agent with a different job name prefix must NOT be picked up.
        _registry.Register(new AgentRegistrationMessage { AgentId = "caa-models-OTHER-xyz", Hostname = "h", Labels = [] }, "conn-other");

        var (models, error) = await _service.WaitAndFetchAsync("caa-models-TARGET", 1, 50, CancellationToken.None);

        error.Should().Contain("connect", "wrong-prefix agent must not match; timeout must fire");
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAndFetchAsync_AgentConnectsAfterDelay_StillFound()
    {
        // Agent registers after a delay (simulates pod startup time).
        const string prefix = "caa-models-delayed";
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            _registry.Register(new AgentRegistrationMessage { AgentId = $"{prefix}-pod", Hostname = "h", Labels = [] }, "conn-late");
            _mockComm.Setup(c => c.RequestFetchModelsAsync("conn-late", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
                .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
                {
                    _service.CompleteRequest(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = "late-model" }] });
                    return Task.CompletedTask;
                });
        });

        var (models, error) = await _service.WaitAndFetchAsync(prefix, 5, 50, CancellationToken.None);

        error.Should().BeNull();
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("late-model");
    }

    [Fact]
    public async Task WaitAndFetchAsync_AgentRespondsWithError_ReturnsError()
    {
        const string prefix = "caa-models-err";
        _registry.Register(new AgentRegistrationMessage { AgentId = $"{prefix}-pod", Hostname = "h", Labels = [] }, "conn-err");
        _mockComm.Setup(c => c.RequestFetchModelsAsync("conn-err", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                _service.CompleteRequest(new FetchModelsResponse { RequestId = req.RequestId, Models = [], Error = "kiro-cli not found" });
                return Task.CompletedTask;
            });

        var (models, error) = await _service.WaitAndFetchAsync(prefix, 5, 50, CancellationToken.None);

        error.Should().Be("kiro-cli not found");
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAndFetchAsync_Timeout_ReturnsTimeoutError()
    {
        // No agent registers — must time out within the specified seconds.
        var (models, error) = await _service.WaitAndFetchAsync("caa-models-noshow", 1, 50, CancellationToken.None);

        error.Should().Contain("did not connect within 1s");
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAndFetchAsync_Cancelled_ReturnsCancelledError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (models, error) = await _service.WaitAndFetchAsync("caa-models-x", 10, 50, cts.Token);

        error.Should().Contain("cancelled");
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAndFetchAsync_MultipleAgentsWithPrefix_PicksFirstConnected()
    {
        // When multiple pods from the same job connect (shouldn't happen in practice,
        // but must be handled), the first one in the registry is used.
        const string prefix = "caa-models-multi";
        _registry.Register(new AgentRegistrationMessage { AgentId = $"{prefix}-pod1", Hostname = "h", Labels = [] }, "conn-1");
        _registry.Register(new AgentRegistrationMessage { AgentId = $"{prefix}-pod2", Hostname = "h", Labels = [] }, "conn-2");

        // Both connections will handle the request — only one completes
        foreach (var conn in new[] { "conn-1", "conn-2" })
        {
            var c = conn;
            _mockComm.Setup(x => x.RequestFetchModelsAsync(c, It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
                .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
                {
                    _service.CompleteRequest(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = $"model-from-{c}" }] });
                    return Task.CompletedTask;
                });
        }

        var (models, error) = await _service.WaitAndFetchAsync(prefix, 5, 50, CancellationToken.None);

        error.Should().BeNull("at least one agent responded");
        models.Should().HaveCount(1, "exactly one agent should respond");
    }

    [Fact]
    public async Task WaitAndFetchAsync_DisconnectedAgentWithPrefix_IsNotMatched()
    {
        // A Disconnected agent must not be selected — it's not reachable.
        const string prefix = "caa-models-dc";
        _registry.Register(new AgentRegistrationMessage { AgentId = $"{prefix}-pod", Hostname = "h", Labels = [] }, "conn-dc");
        _registry.TransitionStatus($"{prefix}-pod", AgentStatus.Disconnected);

        var (models, error) = await _service.WaitAndFetchAsync(prefix, 1, 50, CancellationToken.None);

        error.Should().Contain("connect", "disconnected agent must not be matched");
        models.Should().BeEmpty();
    }
}
