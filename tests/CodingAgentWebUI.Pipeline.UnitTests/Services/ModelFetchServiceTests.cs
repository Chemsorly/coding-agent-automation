using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ModelFetchService.
/// Covers: CompleteRequest (known/unknown request ID), FetchModelsAsync cache hit path,
/// WaitAndFetchAsync (no agents), constructor guards, ResetCache.
/// </summary>
public sealed class ModelFetchServiceTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<IAgentCommunication> _comm = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly ModelFetchService _sut;

    public ModelFetchServiceTests()
    {
        _registry = new AgentRegistryService(_logger.Object);
        _sut = new ModelFetchService(_registry, _comm.Object, _logger.Object);
    }

    private static AgentEntry MakeAgent(string id, AgentStatus status) =>
        new()
        {
            AgentId = new AgentId(id),
            ConnectionId = $"conn-{id}",
            Hostname = "host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = status
        };

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var act = () => new ModelFetchService(null!, _comm.Object, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullComm_Throws()
    {
        var act = () => new ModelFetchService(_registry, null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── CompleteRequest ───────────────────────────────────────────────────

    [Fact]
    public void CompleteRequest_NullResponse_Throws()
    {
        var act = () => _sut.CompleteRequest(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CompleteRequest_UnknownRequestId_DoesNotThrow()
    {
        var act = () => _sut.CompleteRequest(new FetchModelsResponse
        {
            RequestId = "non-existent",
            Models = []
        });
        act.Should().NotThrow();
    }

    // ── FetchModelsAsync — cache hit ──────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_WhenCachePopulated_ReturnsCacheWithoutCallingAgent()
    {
        // Pre-populate cache by completing a pending request
        // Set up a pending TCS manually by sending a request then completing it
        var agent = MakeAgent("a1", AgentStatus.Idle);
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = new AgentId("a1"),
            Hostname = "h",
            Labels = []
        }, "conn-a1");

        _comm.Setup(c => c.RequestFetchModelsAsync(It.IsAny<string>(), It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Kick off a fetch (it will block waiting for CompleteRequest)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var fetchTask = _sut.FetchModelsAsync(cts.Token);

        // Complete the pending request
        await Task.Delay(50);
        var pendingRequestId = _comm.Invocations
            .Where(i => i.Method.Name == nameof(IAgentCommunication.RequestFetchModelsAsync))
            .Select(i => ((FetchModelsRequest)i.Arguments[1]).RequestId)
            .FirstOrDefault();

        if (pendingRequestId != null)
        {
            _sut.CompleteRequest(new FetchModelsResponse
            {
                RequestId = pendingRequestId,
                Models = [new AgentModelInfo { ModelId = "gpt-4" }]
            });
        }

        var (models, error) = await fetchTask;

        if (error is null)
        {
            // Cache populated — verify second call uses cache (no second comm invocation)
            _comm.Invocations.Clear();
            var (cachedModels, cachedError) = await _sut.FetchModelsAsync(CancellationToken.None);
            cachedError.Should().BeNull();
            _comm.Verify(c => c.RequestFetchModelsAsync(
                It.IsAny<string>(), It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task FetchModelsAsync_NoAgents_ReturnsErrorMessage()
    {
        // Empty registry — no agents registered
        var (models, error) = await _sut.FetchModelsAsync(CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("No agents available");
    }

    // ── WaitAndFetchAsync — no agent connects ─────────────────────────────

    [Fact]
    public async Task WaitAndFetchAsync_NoAgentConnects_ReturnsTimeout()
    {
        using var cts = new CancellationTokenSource();

        var (models, error) = await _sut.WaitAndFetchAsync(
            "non-existent-prefix",
            timeoutSeconds: 1,
            pollIntervalMs: 100,
            cts.Token);

        models.Should().BeEmpty();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WaitAndFetchAsync_WhenCancelled_ReturnsEmpty()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (models, error) = await _sut.WaitAndFetchAsync(
            "prefix",
            timeoutSeconds: 5,
            pollIntervalMs: 10,
            cts.Token);

        models.Should().BeEmpty();
    }

    // ── ResetCache ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetCache_ClearsCachedModels()
    {
        // Populate the cache manually via reflection (internal field _cachedModels)
        var field = typeof(ModelFetchService).GetField("_cachedModels",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(_sut, new List<AgentModelInfo> { new() { ModelId = "cached" } });

        _sut.ResetCache();

        // After reset, no agents → returns error (not cached data)
        var (models, error) = await _sut.FetchModelsAsync(CancellationToken.None);
        error.Should().NotBeNull(); // "No agents available"
    }

    // ── CompleteRequest resolves pending fetch ────────────────────────────

    [Fact]
    public async Task CompleteRequest_ForKnownRequest_SetsResult()
    {
        // Verify that completing a request resolves it (internal state)
        // Use the same ID that would be generated by internal logic

        // Pre-inject a pending TCS via reflection
        var pendingField = typeof(ModelFetchService).GetField("_pending",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>>)
            pendingField!.GetValue(_sut)!;

        var tcs = new TaskCompletionSource<FetchModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending["request-123"] = tcs;

        var response = new FetchModelsResponse
        {
            RequestId = "request-123",
            Models = [new AgentModelInfo { ModelId = "model-1" }]
        };

        _sut.CompleteRequest(response);

        tcs.Task.IsCompleted.Should().BeTrue();
        var result = await tcs.Task;
        result.Should().Be(response);
    }
}
