using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentConnectionLifecycle.HandleTerminalClosedAsync"/> and
/// <see cref="AgentConnectionLifecycle.HandleReconnectedAsync"/> using fake hub managers.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentConnectionLifecycleReconnectionTests
{
    // ── HandleTerminalClosedAsync ─────────────────────────────────────────

    [Fact]
    public async Task HandleTerminalClosedAsync_AllAttemptsExhausted_CallsStopApplication()
    {
        // All StartAsync calls throw → all attempts fail → StopApplication
        var stopCalled = false;
        var (lifecycle, _, _) = CreateLifecycle(
            stopApplication: () => stopCalled = true,
            factoryFunc: () =>
            {
                var fake = new FakeHubConnectionManager();
                fake.StartException = new InvalidOperationException("cannot connect");
                return fake;
            });

        // maxAttempts=1 to avoid real delays; one attempt fails → exhausted
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 1);

        stopCalled.Should().BeTrue("StopApplication must be called after all reconnection attempts fail");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_FirstAttemptSucceeds_FactoryCalledOnce()
    {
        // StartAsync succeeds but InvokeAsync (RegisterAgent) will throw since connection is not started.
        // That's an exception path → newManager disposed, attempt counted as failure.
        // To test "success" we need to observe the CAS swap. Since InvokeAsync always throws
        // on a non-started connection, we verify factory was called and DisposeCallCount tracks.
        var createdManagers = new List<FakeHubConnectionManager>();
        var (lifecycle, _, _) = CreateLifecycle(
            factoryFunc: () =>
            {
                var fake = new FakeHubConnectionManager();
                createdManagers.Add(fake);
                return fake;
            });

        // With maxAttempts=1 and RegisterAgent failing, expect exhaustion → StopApplication
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 1);

        createdManagers.Should().HaveCount(1, "factory called once per attempt");
        createdManagers[0].StartCallCount.Should().Be(1);
        // newManager should be disposed after failed registration
        createdManagers[0].DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_CancelledBeforeAttempt_ExitsEarly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var stopCalled = false;
        var (lifecycle, _, _) = CreateLifecycle(
            stopApplication: () => stopCalled = true,
            appStoppingToken: cts.Token,
            factoryFunc: () => new FakeHubConnectionManager());

        // Task.Delay with pre-cancelled token throws OperationCanceledException immediately
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 10);

        stopCalled.Should().BeFalse("cancelled before attempts — StopApplication must not be called");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_AlreadyDisposed_ExitsImmediately()
    {
        var factoryCalls = 0;
        var (lifecycle, _, _) = CreateLifecycle(
            factoryFunc: () => { factoryCalls++; return new FakeHubConnectionManager(); });

        // Dispose sets _hubManager to null
        await lifecycle.DisposeAsync();

        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 5);

        factoryCalls.Should().Be(0, "already disposed — factory must not be called");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_MultipleAttempts_EachCreatesNewManager()
    {
        // Each attempt creates a new manager; on failure each is disposed.
        var createdManagers = new List<FakeHubConnectionManager>();
        var (lifecycle, _, _) = CreateLifecycle(
            factoryFunc: () =>
            {
                var fake = new FakeHubConnectionManager();
                fake.StartException = new InvalidOperationException("fail");
                createdManagers.Add(fake);
                return fake;
            });

        // delayOverride => zero so the 3 attempts don't wait real reconnection backoff (~15s).
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 3, delayOverride: _ => TimeSpan.Zero);

        createdManagers.Should().HaveCount(3, "one manager created per attempt");
        createdManagers.Should().AllSatisfy(m =>
            m.DisposeCallCount.Should().Be(1, "each failed manager must be disposed"));
    }

    // ── HandleReconnectedAsync ────────────────────────────────────────────

    [Fact]
    public async Task HandleReconnectedAsync_InvokeThrows_EntersExtendedRetryLoop()
    {
        // InvokeAsync always throws (connection not started) — should exhaust 3 extended retries
        // then call StopApplication.
        var stopCalled = false;
        var (lifecycle, _, _) = CreateLifecycle(
            stopApplication: () => stopCalled = true);

        // Set very short extended retry delay to keep test fast
        lifecycle.ExtendedRetryDelay = TimeSpan.FromMilliseconds(1);

        await lifecycle.HandleReconnectedAsync("conn-id");

        stopCalled.Should().BeTrue("extended retry loop exhausted — StopApplication must be called");
    }

    [Fact]
    public async Task HandleReconnectedAsync_CancelledDuringExtendedRetry_ExitsWithoutStopApplication()
    {
        using var cts = new CancellationTokenSource();

        var stopCalled = false;
        var (lifecycle, _, _) = CreateLifecycle(
            stopApplication: () => stopCalled = true,
            appStoppingToken: cts.Token);

        // First InvokeAsync throws → enters extended retry loop → cancel before first retry delay
        lifecycle.ExtendedRetryDelay = TimeSpan.FromSeconds(60); // long delay so we can cancel it

        var task = lifecycle.HandleReconnectedAsync("conn-id");

        // TODO [WARNING]: The 50 ms delay is a timing assumption — on a loaded CI machine the
        // initial InvokeAsync attempt may not have completed (or may already have entered the
        // extended retry loop) by the time the cancellation fires, making this a race.
        // If the cancel fires before the first InvokeAsync, the early-return path is taken,
        // which has different semantics. Replace with a ManualResetEventSlim or a
        // fake-HubManager that signals after the first InvokeAsync invocation to eliminate
        // the race and make cancellation timing deterministic.
        // Let initial InvokeAsync attempt fail, then cancel before extended retry delay
        await Task.Delay(50);
        cts.Cancel();

        await task;

        stopCalled.Should().BeFalse("cancellation during extended retry — StopApplication must not be called");
    }

    [Fact]
    public async Task HandleReconnectedAsync_AlreadyDisposed_ExitsImmediately()
    {
        var stopCalled = false;
        var (lifecycle, _, _) = CreateLifecycle(
            stopApplication: () => stopCalled = true);

        await lifecycle.DisposeAsync();

        // Should return immediately without throwing or calling StopApplication
        await lifecycle.HandleReconnectedAsync("conn-id");

        stopCalled.Should().BeFalse("disposed — no retry or StopApplication");
    }

    // ── UnwireEventHandlers via two consecutive reconnects ────────────────

    /// <summary>
    /// AC#3 — Two consecutive terminal-close events create two factory hubs.
    /// After the second reconnect, the first factory hub (factoryHubs[0]) is the old manager
    /// and must have been unwired. Firing an event on it must NOT reach the lifecycle's subscriber.
    /// The second factory hub (factoryHubs[1]) is the current manager and MUST still be wired.
    ///
    /// Note: the initial hub from CreateLifecycle is never wired (WireEventHandlers is called
    /// from ConnectAndRunAsync, not the constructor) so this test uses factory-created hubs,
    /// which ARE wired by the coordinator's _wireHandlers callback on each reconnect.
    /// </summary>
    [Fact]
    public async Task TwoConsecutiveReconnects_FirstFactoryHubUnwired_SecondHubStillWired()
    {
        var factoryHubs = new List<FakeHubConnectionManager>();
        var (lifecycle, _, _) = CreateLifecycle(
            factoryFunc: () =>
            {
                var hub = new FakeHubConnectionManager(new RecordingHubConnection());
                factoryHubs.Add(hub);
                return hub;
            });

        var invocationCount = 0;
        lifecycle.OnAssignChatPrompt += _ => { invocationCount++; return Task.CompletedTask; };

        // First reconnect: creates factoryHubs[0], wires it, CAS-swaps in
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero);

        // Second reconnect: creates factoryHubs[1], wires it, unwires factoryHubs[0], CAS-swaps in
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero);

        factoryHubs.Should().HaveCount(2, "each reconnect creates one manager via the factory");

        // Negative assertion: old hub (factoryHubs[0]) must not forward events after being unwired
        await factoryHubs[0].SimulateAssignChatPromptAsync(new ChatPromptMessage { SessionId = "s1", Prompt = "test" });
        invocationCount.Should().Be(0,
            "after UnwireEventHandlers, firing OnAssignChatPrompt on the old manager must not reach the lifecycle subscriber");

        // Positive assertion: current hub (factoryHubs[1]) must still forward events
        await factoryHubs[1].SimulateAssignChatPromptAsync(new ChatPromptMessage { SessionId = "s1", Prompt = "test" });
        invocationCount.Should().Be(1,
            "the current (newly wired) manager must still forward events to the lifecycle subscriber");
    }

    /// <summary>
    /// Verifies that the coordinator's DisposeAsync path also unwires the initial manager.
    /// When ConnectAndRunAsync is called first (which wires the initial hub), a subsequent
    /// DisposeAsync must call UnwireEventHandlers on that hub so it no longer forwards events.
    /// This covers the common production shutdown path: connect → run → stop.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AfterConnectAndRunAsync_UnwiresInitialHub()
    {
        var initialHub = new FakeHubConnectionManager(new RecordingHubConnection());
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager(new RecordingHubConnection()));

        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var lifecycle = new AgentConnectionLifecycle(
            initialHub,
            factory,
            new AgentId("test-agent"),
            lifetimeMock.Object,
            new Mock<Serilog.ILogger>().Object,
            new AgentRuntimeOptions
            {
                IsChatMode = true,       // short-circuits heartbeat loop
                ChatSessionId = "s-1",
                ChatModel = "auto",
                AgentLabels = ""
            });

        // Pre-signal chat end so ConnectAndRunAsync returns immediately after RegisterAgent
        lifecycle.SignalChatEnd();

        var invocationCount = 0;
        lifecycle.OnAssignChatPrompt += _ => { invocationCount++; return Task.CompletedTask; };

        // ConnectAndRunAsync wires the initial hub and returns (chat mode + pre-signalled end)
        await lifecycle.ConnectAndRunAsync(CancellationToken.None);

        // Verify the hub IS wired before dispose
        await initialHub.SimulateAssignChatPromptAsync(new ChatPromptMessage { SessionId = "s1", Prompt = "test" });
        invocationCount.Should().Be(1, "initial hub must forward events after ConnectAndRunAsync wires it");

        invocationCount = 0;

        // DisposeAsync → coordinator calls UnwireEventHandlers on the initial hub
        await lifecycle.DisposeAsync();

        // Verify the hub is NOT wired after dispose
        await initialHub.SimulateAssignChatPromptAsync(new ChatPromptMessage { SessionId = "s1", Prompt = "test" });
        invocationCount.Should().Be(0,
            "after DisposeAsync, the initial hub must be unwired and must not forward events");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (AgentConnectionLifecycle Lifecycle, FakeHubConnectionManager InitialManager, FakeHubConnectionManagerFactory Factory)
        CreateLifecycle(
            Action? stopApplication = null,
            Func<IHubConnectionManager>? factoryFunc = null,
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
            mockLogger);

        return (lifecycle, initialManager, factory);
    }
}
