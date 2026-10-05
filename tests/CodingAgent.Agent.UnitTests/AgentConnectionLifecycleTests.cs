using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentConnectionLifecycle"/>:
/// - IAsyncDisposable interface compliance
/// - Atomic disposal via Interlocked.Exchange
/// - Race-freedom between DisposeAsync and ShutdownAsync
/// - Idempotent disposal
/// - Constructor null guards and chat-mode fields from <see cref="AgentRuntimeOptions"/>
/// - <see cref="AgentConnectionLifecycle.SignalChatEnd"/> and graceful <see cref="AgentConnectionLifecycle.ShutdownAsync"/> paths
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentConnectionLifecycleTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(AgentConnectionLifecycle))
            .Should().BeTrue("AgentConnectionLifecycle must implement IAsyncDisposable to prevent connection leaks");
    }

    [Fact]
    public void Instance_IsAssignableTo_IAsyncDisposable()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();
        lifecycle.Should().BeAssignableTo<IAsyncDisposable>();
    }

    // ── DisposeAsync behavior ────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        var act = async () => await lifecycle.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent_DoesNotThrow()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        // First disposal
        await lifecycle.DisposeAsync();

        // Second disposal — must not throw
        var act = async () => await lifecycle.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // TODO: These two tests validate null-guard behavior of property getters but not actual resource
    // disposal. They would pass if DisposeAsync simply set _hubManager = null without calling
    // SafeDisposeAsync(manager). Add a test that verifies HubConnectionManager.DisposeAsync() is
    // actually invoked (e.g., via a mock or spy) to catch connection leak regressions.
    [Fact]
    public async Task DisposeAsync_NullsHubManager_IsConnectedReturnsFalse()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        await lifecycle.DisposeAsync();

        lifecycle.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_NullsHubManager_ConnectionThrowsObjectDisposedException()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        await lifecycle.DisposeAsync();

        var act = () => lifecycle.Connection;
        act.Should().Throw<ObjectDisposedException>();
    }

    // ── DisposeAsync + ShutdownAsync race freedom ────────────────────────

    [Fact]
    public async Task DisposeAsync_ThenShutdownAsync_DoesNotThrow()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        await lifecycle.DisposeAsync();

        // ShutdownAsync after disposal must not throw (NRE or ObjectDisposedException)
        var act = async () => await lifecycle.ShutdownAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ShutdownAsync_ThenDisposeAsync_DoesNotThrow()
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        // ShutdownAsync first (will fail to deregister since not connected, but should not throw)
        await lifecycle.ShutdownAsync();

        // Then DisposeAsync — must not throw
        var act = async () => await lifecycle.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // TODO: This test validates exception suppression but not atomicity — it would pass even if
    // Interlocked.Exchange were replaced with a plain null assignment. The HubConnectionManager is
    // never started, so the race window is effectively empty. Consider starting the connection
    // or using a spy to verify exactly-once disposal.
    [Fact]
    public async Task ConcurrentDisposeAndShutdown_NoObjectDisposedException()
    {
        // Run multiple concurrent calls to verify no ObjectDisposedException
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(lifecycle.DisposeAsync().AsTask());
            tasks.Add(lifecycle.ShutdownAsync());
        }

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    // ── Constructor null guards ───────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnNullFactory()
    {
        var mockLogger = new Mock<Serilog.ILogger>();

        var act = () => new AgentConnectionLifecycle(
            CreateTestHubManager(),
            null!,
            new AgentId("test"),
            Mock.Of<IHostApplicationLifetime>(),
            mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

    // ── SignalChatEnd ─────────────────────────────────────────────────────

    [Fact]
    public void SignalChatEnd_Idempotent_SecondCallDoesNotThrow()
    {
        var (lifecycle, _, _) = CreateLifecycle();

        lifecycle.SignalChatEnd();
        var act = () => lifecycle.SignalChatEnd();

        act.Should().NotThrow("SignalChatEnd uses TrySetResult — second call is a safe no-op");
    }

    [Fact]
    public void SignalChatEnd_CompletesTheChatEndSource()
    {
        var (lifecycle, _, _) = CreateLifecycle();

        lifecycle.SignalChatEnd();

        lifecycle._chatEndSource.Task.IsCompleted.Should().BeTrue(
            "SignalChatEnd must resolve the TaskCompletionSource");
    }

    // ── Constructor: chat-mode fields from AgentRuntimeOptions ───────────

    [Fact]
    public void Constructor_WithRuntimeOptions_SetsChatModeFields()
    {
        var options = new AgentRuntimeOptions
        {
            IsChatMode = true,
            ChatSessionId = "session-abc",
            ChatModel = "claude-3-5-sonnet",
            ChatEffort = "medium",
            AgentLabels = "kiro,dotnet"
        };

        var (lifecycle, _, _) = CreateLifecycle(runtimeOptions: options);

        lifecycle._isChatMode.Should().BeTrue("IsChatMode from options must be applied");
        lifecycle._chatSessionId.Should().Be("session-abc");
        lifecycle._chatModel.Should().Be("claude-3-5-sonnet");
        lifecycle._chatEffort.Should().Be("medium");
    }

    [Fact]
    public void Constructor_WithNullRuntimeOptions_FallsBackToEnvVars()
    {
        // Without env vars set, defaults should be false/""/null
        var (lifecycle, _, _) = CreateLifecycle(runtimeOptions: null);

        // These defaults hold when env vars are not set (EnvironmentVariables collection prevents interference)
        lifecycle._isChatMode.Should().BeFalse("default when AGENT_CHAT_MODE is not set");
        lifecycle._chatSessionId.Should().Be("", "default when AGENT_CHAT_SESSION_ID is not set");
    }

    // ── IsConnected / Connection after dispose ────────────────────────────

    [Fact]
    public async Task IsConnected_AfterDispose_ReturnsFalse()
    {
        var (lifecycle, _, _) = CreateLifecycle();
        await lifecycle.DisposeAsync();

        lifecycle.IsConnected.Should().BeFalse("disposed lifecycle must report IsConnected=false");
    }

    [Fact]
    public async Task Connection_AfterDispose_ThrowsObjectDisposedException()
    {
        var (lifecycle, _, _) = CreateLifecycle();
        await lifecycle.DisposeAsync();

        var act = () => _ = lifecycle.Connection;
        act.Should().Throw<ObjectDisposedException>("accessing Connection after dispose must throw");
    }

    // ── ShutdownAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ShutdownAsync_AfterDispose_DoesNotThrow()
    {
        var (lifecycle, _, _) = CreateLifecycle();
        await lifecycle.DisposeAsync();

        var act = async () => await lifecycle.ShutdownAsync();
        await act.Should().NotThrowAsync("ShutdownAsync on disposed lifecycle is a no-op");
    }

    [Fact]
    public async Task ShutdownAsync_NotConnected_DoesNotThrow()
    {
        var (lifecycle, _, _) = CreateLifecycle();

        // FakeHubConnectionManager.IsConnected is always false — ShutdownAsync should
        // skip the deregister invocation and just call StopAsync
        var act = async () => await lifecycle.ShutdownAsync();
        await act.Should().NotThrowAsync("shutdown when not connected must be graceful");
    }

    // ── DisposeAsync: exactly-once disposal via Interlocked.Exchange ──────

    [Fact]
    public async Task DisposeAsync_CalledTwice_DisposesHubOnce()
    {
        var (lifecycle, initialManager, _) = CreateLifecycle();

        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();

        // The initial hub manager should be disposed exactly once
        initialManager.DisposeCallCount.Should().Be(1,
            "Interlocked.Exchange guarantees exactly-once disposal even on double DisposeAsync");
    }

    // ── HandleTerminalClosedAsync: already-disposed exits early ──────────

    [Fact]
    public async Task HandleTerminalClosed_AfterDispose_FactoryNeverCalled()
    {
        var factoryCalls = 0;
        var (lifecycle, _, _) = CreateLifecycle(
            factoryFunc: () => { factoryCalls++; return new FakeHubConnectionManager(); });

        await lifecycle.DisposeAsync();
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 5);

        factoryCalls.Should().Be(0, "disposed lifecycle must not attempt reconnection");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (AgentConnectionLifecycle Lifecycle, FakeHubConnectionManager InitialManager,
        FakeHubConnectionManagerFactory Factory) CreateLifecycle(
            Action? stopApplication = null,
            Func<IHubConnectionManager>? factoryFunc = null,
            AgentRuntimeOptions? runtimeOptions = null,
            CancellationToken appStoppingToken = default)
    {
        var mockLogger = new Mock<Serilog.ILogger>().Object;
        var initialManager = new FakeHubConnectionManager();

        var factory = new FakeHubConnectionManagerFactory(
            factoryFunc ?? (() => new FakeHubConnectionManager()));

        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.ApplicationStopping).Returns(appStoppingToken);
        if (stopApplication is not null)
            lifetimeMock.Setup(l => l.StopApplication()).Callback(stopApplication);

        var lifecycle = new AgentConnectionLifecycle(
            initialManager,
            factory,
            new AgentId("test-agent"),
            lifetimeMock.Object,
            mockLogger,
            runtimeOptions);

        return (lifecycle, initialManager, factory);
    }

    private static HubConnectionManager CreateTestHubManager()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManager("http://localhost:9999", "test-agent", "test-api-key", logger.Object);
    }
}
