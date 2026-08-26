using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Additional coverage tests for <see cref="AgentConnectionLifecycle"/> targeting paths
/// not yet covered by existing test files:
///
/// - <see cref="AgentConnectionLifecycle.ShouldDropBufferedMessage"/> — static predicate
/// - <see cref="AgentConnectionLifecycle.SignalChatEnd"/> — idempotent TCS completion
/// - Constructor chat-mode fields populated from <see cref="AgentRuntimeOptions"/>
/// - <see cref="AgentConnectionLifecycle.ShutdownAsync"/> graceful paths
/// - <see cref="AgentConnectionLifecycle.IsConnected"/> and <see cref="AgentConnectionLifecycle.Connection"/> after dispose
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class AgentConnectionLifecycleAdditionalTests
{
    // ── ShouldDropBufferedMessage (static predicate) ──────────────────────

    private static JobCompletionPayload MakePayload(PipelineStep step = PipelineStep.Completed)
        => new() { FinalStep = step, CompletedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void ShouldDropBufferedMessage_AttemptsBeforeMax_ReturnsFalse()
    {
        var msg = new BufferedJobCompleted("job-1", MakePayload(), DateTimeOffset.UtcNow);
        // DrainAttempts starts at 0; maxDrainAttempts = 3
        AgentConnectionLifecycle.ShouldDropBufferedMessage(msg, maxDrainAttempts: 3)
            .Should().BeFalse("0 < 3 — message should be kept");
    }

    [Fact]
    public void ShouldDropBufferedMessage_AttemptsEqualMax_ReturnsTrue()
    {
        var msg = new BufferedJobCompleted("job-1", MakePayload(PipelineStep.Failed), DateTimeOffset.UtcNow);
        // Simulate 3 prior drain attempts
        for (var i = 0; i < 3; i++) msg = msg with { DrainAttempts = msg.DrainAttempts + 1 };

        AgentConnectionLifecycle.ShouldDropBufferedMessage(msg, maxDrainAttempts: 3)
            .Should().BeTrue("DrainAttempts(3) >= max(3) — message should be dropped");
    }

    [Fact]
    public void ShouldDropBufferedMessage_AttemptsExceedMax_ReturnsTrue()
    {
        var msg = new BufferedJobCompleted("job-1", MakePayload(PipelineStep.Cancelled), DateTimeOffset.UtcNow);
        for (var i = 0; i < 5; i++) msg = msg with { DrainAttempts = msg.DrainAttempts + 1 };

        AgentConnectionLifecycle.ShouldDropBufferedMessage(msg, maxDrainAttempts: 3)
            .Should().BeTrue("DrainAttempts(5) >= max(3) — message should be dropped");
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, 3, false)]
    [InlineData(1, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    [InlineData(10, 3, true)]
    public void ShouldDropBufferedMessage_VariousAttemptCounts(int attempts, int max, bool shouldDrop)
    {
        var msg = new BufferedJobCompleted("job-x",
            MakePayload(),
            DateTimeOffset.UtcNow) { DrainAttempts = attempts };

        AgentConnectionLifecycle.ShouldDropBufferedMessage(msg, max)
            .Should().Be(shouldDrop, $"attempts={attempts}, max={max}");
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

        var buffer = new CriticalMessageBuffer();
        var signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(mockLogger);
        var signalRReporter = new SignalRCompletionReporter(initialManager, signalRPipeline, buffer, mockLogger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);

        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.ApplicationStopping).Returns(appStoppingToken);
        if (stopApplication is not null)
            lifetimeMock.Setup(l => l.StopApplication()).Callback(stopApplication);

        var lifecycle = new AgentConnectionLifecycle(
            initialManager,
            factory,
            signalRReporter,
            slotManager,
            new AgentId("test-agent"),
            lifetimeMock.Object,
            mockLogger,
            runtimeOptions);

        return (lifecycle, initialManager, factory);
    }
}
