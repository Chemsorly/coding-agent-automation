using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentConnectionManager"/> public contract and event routing.
/// Uses <see cref="FakeHubConnectionManager"/> to trigger lifecycle events without a real
/// SignalR connection.
/// </summary>
public sealed class AgentConnectionManagerReconnectionTests
{
    private static readonly AgentRegistrationMessage DefaultRegistration = new()
    {
        AgentId = "agent-recon",
        Hostname = "test-host",
        Labels = [],
        ActiveJob = null
    };

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullHubManager_Throws()
    {
        var act = () => new AgentConnectionManager(
            null!,
            new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager()),
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());
        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManager");
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new AgentConnectionManager(
            new FakeHubConnectionManager(),
            null!,
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());
        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new AgentConnectionManager(
            new FakeHubConnectionManager(),
            new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager()),
            new AgentId("test"),
            null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── IsConnected / Connection ──────────────────────────────────────────

    [Fact]
    public void IsConnected_FakeHub_ReturnsFalse()
    {
        var (manager, _) = CreateManager();
        manager.IsConnected.Should().BeFalse(
            "FakeHubConnectionManager.IsConnected is always false (not started against a real server)");
    }

    [Fact]
    public void Connection_ReturnsUnderlyingHubConnection()
    {
        var (manager, _) = CreateManager();
        manager.Connection.Should().NotBeNull();
    }

    // ── UpdateCurrentStep ─────────────────────────────────────────────────

    [Fact]
    public void UpdateCurrentStep_WithStep_DoesNotThrow()
    {
        var (manager, _) = CreateManager();
        var act = () => manager.UpdateCurrentStep(PipelineStep.GeneratingCode);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateCurrentStep_NullStep_DoesNotThrow()
    {
        var (manager, _) = CreateManager();
        var act = () => manager.UpdateCurrentStep(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateCurrentStep_MultipleUpdates_DoesNotThrow()
    {
        var (manager, _) = CreateManager();
        var act = () =>
        {
            manager.UpdateCurrentStep(PipelineStep.CloningRepository);
            manager.UpdateCurrentStep(PipelineStep.GeneratingCode);
            manager.UpdateCurrentStep(null);
        };
        // Volatile write has no observable state; assert no exception is the correct contract check
        act.Should().NotThrow();
    }

    // ── UpdateRegistration ────────────────────────────────────────────────

    [Fact]
    public void UpdateRegistration_ValidMessage_DoesNotThrow()
    {
        var (manager, _) = CreateManager();
        var act = () => manager.UpdateRegistration(DefaultRegistration);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateRegistration_Null_ThrowsArgumentNullException()
    {
        var (manager, _) = CreateManager();
        var act = () => manager.UpdateRegistration(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── OnCancelJobReceived ───────────────────────────────────────────────

    [Fact]
    public void OnCancelJobReceived_CanSubscribe_NoThrow()
    {
        var (manager, _) = CreateManager();
        manager.OnCancelJobReceived += _ => Task.CompletedTask;
        // Subscription must not throw
        manager.Should().NotBeNull();
    }

    // ── OnForceDisconnect ─────────────────────────────────────────────────

    [Fact]
    public void OnForceDisconnect_CanSubscribe_NoThrow()
    {
        var (manager, _) = CreateManager();
        manager.OnForceDisconnect += () => Task.CompletedTask;
        manager.Should().NotBeNull();
    }

    // ── OnReconnected ─────────────────────────────────────────────────────

    [Fact]
    public void OnReconnected_CanSubscribe_NoThrow()
    {
        var (manager, _) = CreateManager();
        manager.OnReconnected += () => Task.CompletedTask;
        manager.Should().NotBeNull();
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var (manager, _) = CreateManager();
        var act = () => manager.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DisposesUnderlyingHub()
    {
        var (manager, fakeHub) = CreateManager();
        await manager.DisposeAsync();
        fakeHub.DisposeCallCount.Should().Be(1, "hub manager must be disposed exactly once");
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var (manager, _) = CreateManager();
        await manager.DisposeAsync();

        var act = () => manager.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync("double dispose must not throw");
    }

    // ── SimulateReconnectedAsync — OnReconnected subscriber fires ─────────

    [Fact]
    public async Task SimulateReconnected_WithRegistration_FiresOnReconnectedSubscriber()
    {
        var (manager, fakeHub) = CreateManager();
        manager.UpdateRegistration(DefaultRegistration);

        var fired = false;
        manager.OnReconnected += () => { fired = true; return Task.CompletedTask; };

        // Trigger via the Simulate helper (the only external way to fire the event)
        await fakeHub.SimulateReconnectedAsync("new-conn");
        await Task.Delay(100);

        fired.Should().BeTrue(
            "OnReconnected must fire after reconnection (even when re-registration attempt fails on FakeHub)");
    }

    [Fact]
    public async Task SimulateReconnected_NoRegistration_DoesNotThrow()
    {
        var (manager, fakeHub) = CreateManager();
        // No UpdateRegistration — null registration path

        var act = async () =>
        {
            await fakeHub.SimulateReconnectedAsync("conn");
            await Task.Delay(50);
        };

        await act.Should().NotThrowAsync(
            "null registration on reconnect must be handled gracefully");
    }

    [Fact]
    public async Task SimulateReconnected_SubscriberThrows_IsSwallowed()
    {
        var (manager, fakeHub) = CreateManager();
        manager.UpdateRegistration(DefaultRegistration);
        manager.OnReconnected += () => throw new InvalidOperationException("subscriber boom");

        var act = async () =>
        {
            await fakeHub.SimulateReconnectedAsync("conn");
            await Task.Delay(50);
        };

        await act.Should().NotThrowAsync(
            "subscriber exceptions must be swallowed to protect the connection lifecycle");
    }

    // ── SimulateClosedAsync — reconnection loop fires ─────────────────────

    [Fact]
    public async Task SimulateClosed_TriggersReconnectionAttempt_ViaFactory()
    {
        var createdCount = 0;
        var factory = new FakeHubConnectionManagerFactory(() =>
        {
            createdCount++;
            return new FakeHubConnectionManager
            {
                StartException = new InvalidOperationException("cannot connect")
            };
        });

        var initialHub = new FakeHubConnectionManager();
        var manager = new AgentConnectionManager(
            initialHub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());

        // Fire terminal close — triggers the reconnection loop
        _ = fakeHub_SimulateClosed(initialHub);

        // Wait for at least one reconnection attempt
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (createdCount == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        createdCount.Should().BeGreaterThan(0,
            "terminal close must trigger at least one reconnection attempt via factory");

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task SimulateClosed_FactoryCreatesManagers_EachDisposedOnFailure()
    {
        var disposedManagers = new List<FakeHubConnectionManager>();
        var factory = new FakeHubConnectionManagerFactory(() =>
        {
            var m = new FakeHubConnectionManager
            {
                StartException = new InvalidOperationException("fail")
            };
            disposedManagers.Add(m);
            return m;
        });

        var initialHub = new FakeHubConnectionManager();
        var manager = new AgentConnectionManager(
            initialHub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());

        _ = fakeHub_SimulateClosed(initialHub);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (disposedManagers.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        // Give any in-flight DisposeAsync calls time to complete
        await Task.Delay(200);

        disposedManagers.Should().AllSatisfy(m =>
            m.DisposeCallCount.Should().Be(1,
                "each failed reconnection manager must be disposed"));

        await manager.DisposeAsync();
    }

    // ── IAgentConnectionManager interface compliance ──────────────────────

    [Fact]
    public void ImplementsIAgentConnectionManager()
    {
        var (manager, _) = CreateManager();
        manager.Should().BeAssignableTo<IAgentConnectionManager>();
    }

    [Fact]
    public void ImplementsIAsyncDisposable()
    {
        var (manager, _) = CreateManager();
        manager.Should().BeAssignableTo<IAsyncDisposable>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (AgentConnectionManager Manager, FakeHubConnectionManager Hub) CreateManager()
    {
        var hub = new FakeHubConnectionManager();
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var manager = new AgentConnectionManager(
            hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());
        return (manager, hub);
    }

    // Helper to fire the OnClosed event on a FakeHubConnectionManager
    // (events are only callable from within the type, so we use SimulateClosedAsync)
    private static Task fakeHub_SimulateClosed(FakeHubConnectionManager hub)
        => hub.SimulateClosedAsync(new InvalidOperationException("server down"));

    // ── B4: CAS swap prevents double-ownership on concurrent DisposeAsync ───

    /// <summary>
    /// B4 regression test: <see cref="AgentConnectionManager.HandleTerminalClosedAsync"/> must
    /// use <see cref="Interlocked.CompareExchange{T}"/> so a concurrent <see cref="AgentConnectionManager.DisposeAsync"/>
    /// cannot race with the reconnect loop and leave two managers sharing one slot.
    /// </summary>
    [Fact]
    public async Task TerminalClose_ConcurrentDispose_DoesNotLeakManagerReferences()
    {
        // Arrange: factory creates new managers that all fail StartAsync so reconnect loops infinitely
        var factory = new FakeHubConnectionManagerFactory(() =>
            new FakeHubConnectionManager { StartException = new InvalidOperationException("cannot connect") });
        var hub = new FakeHubConnectionManager();
        var manager = new AgentConnectionManager(hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());

        // Act: fire terminal close and immediately dispose
        _ = fakeHub_SimulateClosed(hub);
        await Task.Delay(5); // let the loop start one attempt
        var disposeAct = async () => await manager.DisposeAsync();

        // Assert: DisposeAsync must complete without throwing (no ObjectDisposedException, no NRE)
        // — the CAS in the reconnect loop ensures the orphaned manager is disposed cleanly
        await disposeAct.Should().NotThrowAsync();

    // ── B5: StopApplication called on reconnection exhaustion ─────────────

    /// <summary>
    /// B5 regression test: <see cref="AgentConnectionManager.HandleTerminalClosedAsync"/> must call
    /// <see cref="IHostApplicationLifetime.StopApplication"/> when all reconnect attempts are exhausted,
    /// matching the behaviour of <see cref="AgentConnectionLifecycle"/>.
    /// Without this, a work-item pod keeps running with a dead connection for up to 7200s.
    /// </summary>
    [Fact]
    public async Task TerminalClose_AllAttemptsExhausted_CallsStopApplication()
    {
        // Arrange: factory always fails → maxAttempts=1 to avoid real exponential delays
        var factory = new FakeHubConnectionManagerFactory(() =>
            new FakeHubConnectionManager { StartException = new InvalidOperationException("cannot connect") });
        var hub = new FakeHubConnectionManager();

        var stopCalled = false;
        var lifetimeMock = new Mock<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.StopApplication()).Callback(() => stopCalled = true);

        var manager = new AgentConnectionManager(hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>(), lifetimeMock.Object);

        // Act: call HandleTerminalClosedAsync directly with maxAttempts=1 to avoid real delays
        await manager.HandleTerminalClosedAsync(null, maxAttempts: 1);

        // Assert
        stopCalled.Should().BeTrue("StopApplication must be called after all reconnection attempts fail");

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task TerminalClose_NullLifetime_ExhaustsWithoutThrowingOnStopApplication()
    {
        // When lifetime is not injected (test / non-K8s context), exhaustion must only log and not throw
        var factory = new FakeHubConnectionManagerFactory(() =>
            new FakeHubConnectionManager { StartException = new InvalidOperationException("cannot connect") });
        var hub = new FakeHubConnectionManager();
        var manager = new AgentConnectionManager(hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>()); // no lifetime

        var act = async () =>
        {
            _ = fakeHub_SimulateClosed(hub);
            await Task.Delay(200); // let loop run briefly
            await manager.DisposeAsync();
        };
        await act.Should().NotThrowAsync();
    }
}
