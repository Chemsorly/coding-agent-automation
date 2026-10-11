using AwesomeAssertions;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests.Health;

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
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                // Simulate agent response via CompleteRequestAsync
                await _service.CompleteRequestAsync(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [new AgentModelInfo { ModelId = "claude-sonnet-4-20250514" }]
                });
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
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                await _service.CompleteRequestAsync(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [new AgentModelInfo { ModelId = "model-1" }]
                });
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
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                await _service.CompleteRequestAsync(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [],
                    Error = "CLI not configured"
                });
            });

        var (models, error) = await _service.FetchModelsAsync(CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Be("CLI not configured");
    }

    [Fact]
    public async Task CompleteRequestAsync_UnknownRequestId_NoRedis_LogsWarning()
    {
        await _service.CompleteRequestAsync(new FetchModelsResponse
        {
            RequestId = "unknown-id",
            Models = []
        });

        // Without Redis, an unknown request ID must log a warning.
        // Serilog's ILogger.Warning has a generic<T> overload used with a single property value.
        // TODO: [WARNING] The It.IsAny<string>() matcher for both arguments is too broad — it would match
        // any Warning call with a single string property (e.g. from registry internals). Tighten to also
        // verify the message template contains "FetchModelsResponse" or "unknown request" to lock in the
        // contract.
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRequestAsync_NullResponse_ThrowsArgumentNullException()
    {
        var act = async () => await _service.CompleteRequestAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
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
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                await _service.CompleteRequestAsync(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = "cached" }] });
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
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                await _service.CompleteRequestAsync(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = "m1" }] });
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
        // Mock must be configured BEFORE Register to avoid a race: once the service
        // sees the agent in the registry it immediately calls RequestFetchModelsAsync,
        // so the setup must already be in place at that point.
        const string prefix = "caa-models-delayed";
        _mockComm.Setup(c => c.RequestFetchModelsAsync("conn-late", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                await _service.CompleteRequestAsync(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = "late-model" }] });
            });
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            _registry.Register(new AgentRegistrationMessage { AgentId = $"{prefix}-pod", Hostname = "h", Labels = [] }, "conn-late");
        });

        var (models, error) = await _service.WaitAndFetchAsync(prefix, 30, 50, CancellationToken.None);

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
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                await _service.CompleteRequestAsync(new FetchModelsResponse { RequestId = req.RequestId, Models = [], Error = "kiro-cli not found" });
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
                .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
                {
                    await _service.CompleteRequestAsync(new FetchModelsResponse { RequestId = req.RequestId, Models = [new AgentModelInfo { ModelId = $"model-from-{c}" }] });
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

    // ── ResetCache ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetCache_ClearsCachedModels()
    {
        // Populate the cache manually via reflection (internal field _cachedModels)
        var field = typeof(ModelFetchService).GetField("_cachedModels",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(_service, new List<AgentModelInfo> { new() { ModelId = "cached" } });

        _service.ResetCache();

        // After reset, no agents → returns error (not cached data)
        var (models, error) = await _service.FetchModelsAsync(CancellationToken.None);
        error.Should().NotBeNull(); // "No agents available"
    }

    // ── CompleteRequestAsync resolves pending fetch ────────────────────────────

    [Fact]
    public async Task CompleteRequestAsync_ForKnownRequest_SetsResult()
    {
        // Verify that completing a request resolves it (internal state)
        // Pre-inject a pending TCS via reflection
        var pendingField = typeof(ModelFetchService).GetField("_pending",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>>)
            pendingField!.GetValue(_service)!;

        var tcs = new TaskCompletionSource<FetchModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending["request-123"] = tcs;

        var response = new FetchModelsResponse
        {
            RequestId = "request-123",
            Models = [new AgentModelInfo { ModelId = "model-1" }]
        };

        await _service.CompleteRequestAsync(response);

        tcs.Task.IsCompleted.Should().BeTrue();
        var result = await tcs.Task;
        result.Should().Be(response);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cross-replica Redis tests
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Test 1 (TDD anchor) — A fetch started on service A returns models when the
    /// result is reported to service B (which shares the same FakeRedisStore).
    /// This test must FAIL before the production fix and PASS after.
    /// </summary>
    [Fact]
    public async Task CrossReplica_ResultReportedToOtherInstance_FetchCompletes()
    {
        var fakeRedis = new FakeRedisStore();
        var logger = new Mock<ILogger>();
        var registry = new AgentRegistryService(logger.Object);

        // serviceA: the replica that sends the request and waits.
        var capturedRequestId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockCommA = new Mock<IAgentCommunication>();
        var serviceA = new ModelFetchService(
            registry, mockCommA.Object, logger.Object,
            redis: fakeRedis,
            // TODO: [WARNING] The 500 ms poll interval is a significant fraction of this 2000 ms
            // responseTimeout, leaving only ~3 polling windows. On a slow CI host, scheduler starvation
            // could cause the fetch to time out before the poll fires, making the test flaky. Consider
            // reducing the poll interval for tests or increasing this timeout.
            responseTimeout: TimeSpan.FromMilliseconds(2000));

        // serviceB: the replica that receives the agent's result (no pending TCS on B).
        var mockCommB = new Mock<IAgentCommunication>();
        var serviceB = new ModelFetchService(
            registry, mockCommB.Object, logger.Object,
            redis: fakeRedis,
            responseTimeout: TimeSpan.FromMilliseconds(2000));

        // Register an agent so serviceA can find one.
        registry.Register(new AgentRegistrationMessage { AgentId = "fetch-pod-1", Hostname = "h", Labels = [] }, "conn-pod-1");

        // serviceA's mock comm captures the requestId and does NOT complete the TCS.
        mockCommA.Setup(c => c.RequestFetchModelsAsync("conn-pod-1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                capturedRequestId.TrySetResult(req.RequestId);
                return Task.CompletedTask; // deliberately does NOT call CompleteRequestAsync
            });

        // Start the fetch on serviceA in the background.
        var fetchTask = serviceA.FetchModelsAsync(CancellationToken.None);

        // Wait until serviceA has registered its pending entry (i.e. the comm mock was called).
        var requestId = await capturedRequestId.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Deliver the result via serviceB — simulating the cross-replica path.
        await serviceB.CompleteRequestAsync(new FetchModelsResponse
        {
            RequestId = requestId,
            Models = [new AgentModelInfo { ModelId = "cross-replica-model" }]
        });

        // serviceA must return the models (not time out).
        var (models, error) = await fetchTask.WaitAsync(TimeSpan.FromSeconds(5));

        error.Should().BeNull("cross-replica delivery must succeed");
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("cross-replica-model");
    }

    /// <summary>
    /// Test 2 — Same-replica completion still works when Redis is configured.
    /// The Redis key must NOT be set (fast path via TCS).
    /// </summary>
    [Fact]
    public async Task SameReplica_WithRedis_CompletesViaLocalTcs_NoRedisKeyWritten()
    {
        var fakeRedis = new FakeRedisStore();
        var logger = new Mock<ILogger>();
        var registry = new AgentRegistryService(logger.Object);

        string? capturedRequestId = null;
        var mockComm = new Mock<IAgentCommunication>();
        var service = new ModelFetchService(
            registry, mockComm.Object, logger.Object,
            redis: fakeRedis,
            responseTimeout: TimeSpan.FromMilliseconds(2000));

        registry.Register(new AgentRegistrationMessage { AgentId = "agent-same-1", Hostname = "h", Labels = [] }, "conn-same-1");

        // The mock calls CompleteRequestAsync on the SAME service instance — same-replica path.
        mockComm.Setup(c => c.RequestFetchModelsAsync("conn-same-1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>(async (_, req, _) =>
            {
                capturedRequestId = req.RequestId;
                await service.CompleteRequestAsync(new FetchModelsResponse
                {
                    RequestId = req.RequestId,
                    Models = [new AgentModelInfo { ModelId = "same-replica-model" }]
                });
            });

        var (models, error) = await service.FetchModelsAsync(CancellationToken.None);

        error.Should().BeNull();
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("same-replica-model");

        // Redis key must NOT have been set — the TCS path completed before any Redis write.
        var redisKey = $"fetch-models:result:{capturedRequestId}";
        (await fakeRedis.ExistsAsync(redisKey)).Should().BeFalse(
            "same-replica path must complete via TCS without writing to Redis");
    }

    /// <summary>
    /// Test 3 — Without Redis, a result reported to B logs a warning on B and A times out.
    /// This verifies the existing no-Redis behavior is unchanged.
    /// </summary>
    [Fact]
    public async Task NoRedis_ResultReportedToOtherInstance_ATimesOutAndBLogsWarning()
    {
        var loggerA = new Mock<ILogger>();
        var loggerB = new Mock<ILogger>();
        var registry = new AgentRegistryService(loggerA.Object);

        var capturedRequestId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockCommA = new Mock<IAgentCommunication>();
        var serviceA = new ModelFetchService(
            registry, mockCommA.Object, loggerA.Object,
            redis: null,
            responseTimeout: TimeSpan.FromMilliseconds(300));

        var mockCommB = new Mock<IAgentCommunication>();
        var serviceB = new ModelFetchService(
            registry, mockCommB.Object, loggerB.Object,
            redis: null,
            responseTimeout: TimeSpan.FromMilliseconds(300));

        registry.Register(new AgentRegistrationMessage { AgentId = "agent-norx-1", Hostname = "h", Labels = [] }, "conn-norx-1");

        mockCommA.Setup(c => c.RequestFetchModelsAsync("conn-norx-1", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                capturedRequestId.TrySetResult(req.RequestId);
                return Task.CompletedTask; // does NOT complete — simulates cross-replica scenario
            });

        var fetchTask = serviceA.FetchModelsAsync(CancellationToken.None);

        var requestId = await capturedRequestId.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Deliver result to B (no Redis — B will log a warning and drop it).
        await serviceB.CompleteRequestAsync(new FetchModelsResponse
        {
            RequestId = requestId,
            Models = [new AgentModelInfo { ModelId = "dropped-model" }]
        });

        // serviceA must time out (no Redis to relay the result).
        // TODO: [WARNING] fetchTask is awaited without a WaitAsync guard. serviceA uses a 300 ms
        // responseTimeout so it completes quickly in practice, but if the timeout were accidentally
        // removed or extended (e.g. missing constructor override), this test would block CI for up to
        // 30 s. Add .WaitAsync(TimeSpan.FromSeconds(5)) to bound the test like the cross-replica tests do.
        var (models, error) = await fetchTask;

        // TODO: [WARNING] The ".Contain("timed out")" assertion is too loose — any message containing
        // "timed out" would pass. Consider asserting the exact contractual message
        // "Request timed out — the agent did not respond in time." to lock in the spec.
        error.Should().Contain("timed out", "without Redis, A cannot receive B's result");
        models.Should().BeEmpty();

        // serviceB must have logged the warning for the unknown request.
        loggerB.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<string>()), Times.Once,
            "serviceB must log a warning for the unknown request ID when Redis is not configured");
    }

    /// <summary>
    /// Test 4 — After A reads the cross-replica result, the Redis key is deleted.
    /// </summary>
    [Fact]
    public async Task CrossReplica_AfterFetchCompletes_RedisKeyIsDeleted()
    {
        var fakeRedis = new FakeRedisStore();
        var logger = new Mock<ILogger>();
        var registry = new AgentRegistryService(logger.Object);

        var capturedRequestId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockCommA = new Mock<IAgentCommunication>();
        var serviceA = new ModelFetchService(
            registry, mockCommA.Object, logger.Object,
            redis: fakeRedis,
            // TODO: [WARNING] Same timing sensitivity as Test 1: 500 ms poll / 2000 ms timeout leaves
            // only ~3 polling windows. Flakiness is possible on a slow CI host. Consider reducing the
            // poll interval or increasing the timeout to add more headroom.
            responseTimeout: TimeSpan.FromMilliseconds(2000));

        var mockCommB = new Mock<IAgentCommunication>();
        var serviceB = new ModelFetchService(
            registry, mockCommB.Object, logger.Object,
            redis: fakeRedis,
            responseTimeout: TimeSpan.FromMilliseconds(2000));

        registry.Register(new AgentRegistrationMessage { AgentId = "fetch-pod-cleanup", Hostname = "h", Labels = [] }, "conn-pod-cleanup");

        mockCommA.Setup(c => c.RequestFetchModelsAsync("conn-pod-cleanup", It.IsAny<FetchModelsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, FetchModelsRequest, CancellationToken>((_, req, _) =>
            {
                capturedRequestId.TrySetResult(req.RequestId);
                return Task.CompletedTask;
            });

        var fetchTask = serviceA.FetchModelsAsync(CancellationToken.None);

        var requestId = await capturedRequestId.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await serviceB.CompleteRequestAsync(new FetchModelsResponse
        {
            RequestId = requestId,
            Models = [new AgentModelInfo { ModelId = "cleanup-model" }]
        });

        await fetchTask.WaitAsync(TimeSpan.FromSeconds(5));

        // TODO: [WARNING] This test only asserts key deletion but never asserts that the fetch returned
        // the correct result. A bug where the key is deleted but the wrong models are returned would go
        // undetected. Add an assertion on the models returned by fetchTask (e.g. models[0].ModelId == "cleanup-model").

        // The Redis key must have been deleted after A read the result.
        var redisKey = $"fetch-models:result:{requestId}";
        (await fakeRedis.GetAsync(redisKey)).Should().BeNull(
            "the result key must be deleted after the waiting replica reads it");
    }
}
