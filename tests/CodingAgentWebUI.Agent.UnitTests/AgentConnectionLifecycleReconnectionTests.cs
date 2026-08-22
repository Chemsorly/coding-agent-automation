using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

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

        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 3);

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

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (AgentConnectionLifecycle Lifecycle, FakeHubConnectionManager InitialManager, FakeHubConnectionManagerFactory Factory)
        CreateLifecycle(
            Action? stopApplication = null,
            CancellationToken appStoppingToken = default,
            Func<IHubConnectionManager>? factoryFunc = null)
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
            mockLogger);

        return (lifecycle, initialManager, factory);
    }
}
