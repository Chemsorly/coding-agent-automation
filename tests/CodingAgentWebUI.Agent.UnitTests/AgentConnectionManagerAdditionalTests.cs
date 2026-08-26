using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Additional coverage tests for <see cref="AgentConnectionManager"/> targeting paths
/// not covered by <see cref="AgentConnectionManagerReconnectionTests"/>:
///
/// - <see cref="AgentConnectionManager.HandleCancelJobAsync"/> — subscriber routing and error swallowing
/// - <see cref="AgentConnectionManager.HandleForceDisconnectAsync"/> — subscriber routing
/// - <see cref="AgentConnectionManager.HandleReconnectedAsync"/> — with/without registration, subscriber firing
/// - <see cref="AgentConnectionManager.InvokeAsync"/> — delegation to action
/// </summary>
public sealed class AgentConnectionManagerAdditionalTests
{
    private static readonly AgentRegistrationMessage DefaultRegistration = new()
    {
        AgentId = "agent-1",
        Hostname = "host-1",
        Labels = [],
        ActiveJob = null
    };

    // ── HandleCancelJobAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CancelJob_WithSubscriber_ForwardsJobIdToSubscriber()
    {
        var (manager, hub) = CreateManager();

        string? received = null;
        manager.OnCancelJobReceived += jobId => { received = jobId; return Task.CompletedTask; };

        await hub.SimulateCancelJobAsync("job-99");
        await Task.Delay(50); // let fire-and-forget handler settle

        received.Should().Be("job-99", "OnCancelJobReceived must forward the exact jobId");
    }

    [Fact]
    public async Task CancelJob_NoSubscriber_DoesNotThrow()
    {
        var (manager, hub) = CreateManager();
        _ = manager; // manager referenced to prevent disposal

        var act = async () =>
        {
            await hub.SimulateCancelJobAsync("job-no-subscriber");
            await Task.Delay(30);
        };

        await act.Should().NotThrowAsync("cancel with no subscriber must be a silent no-op");
    }

    [Fact]
    public async Task CancelJob_SubscriberThrows_ExceptionIsSwallowed()
    {
        var (manager, hub) = CreateManager();
        manager.OnCancelJobReceived += _ => throw new InvalidOperationException("subscriber boom");

        var act = async () =>
        {
            await hub.SimulateCancelJobAsync("job-boom");
            await Task.Delay(50);
        };

        await act.Should().NotThrowAsync("subscriber exceptions must be swallowed to protect lifecycle");
    }

    // ── HandleForceDisconnectAsync ────────────────────────────────────────

    [Fact]
    public async Task ForceDisconnect_WithSubscriber_FiresOnForceDisconnect()
    {
        var (manager, hub) = CreateManager();

        var fired = false;
        manager.OnForceDisconnect += () => { fired = true; return Task.CompletedTask; };

        await hub.SimulateForceDisconnectAsync();
        await Task.Delay(50);

        fired.Should().BeTrue("OnForceDisconnect subscriber must fire when ForceDisconnect is received");
    }

    [Fact]
    public async Task ForceDisconnect_NoSubscriber_DoesNotThrow()
    {
        var (manager, hub) = CreateManager();
        _ = manager;

        var act = async () =>
        {
            await hub.SimulateForceDisconnectAsync();
            await Task.Delay(30);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ForceDisconnect_SubscriberThrows_ExceptionIsSwallowed()
    {
        var (manager, hub) = CreateManager();
        manager.OnForceDisconnect += () => throw new InvalidOperationException("subscriber boom");

        var act = async () =>
        {
            await hub.SimulateForceDisconnectAsync();
            await Task.Delay(50);
        };

        await act.Should().NotThrowAsync("ForceDisconnect subscriber exceptions must be swallowed");
    }

    // ── HandleReconnectedAsync ────────────────────────────────────────────

    [Fact]
    public async Task Reconnected_WithRegistration_FiresOnReconnectedSubscriber()
    {
        var (manager, hub) = CreateManager();
        manager.UpdateRegistration(DefaultRegistration);

        var fired = false;
        manager.OnReconnected += () => { fired = true; return Task.CompletedTask; };

        await hub.SimulateReconnectedAsync("new-conn");
        await Task.Delay(100); // allow async re-registration + subscriber to complete

        fired.Should().BeTrue("OnReconnected subscriber must fire after reconnect attempt");
    }

    [Fact]
    public async Task Reconnected_WithoutRegistration_DoesNotFireOnReconnectedSubscriber()
    {
        // No UpdateRegistration → null registration → HandleReconnectedAsync returns early
        var (manager, hub) = CreateManager();

        var fired = false;
        manager.OnReconnected += () => { fired = true; return Task.CompletedTask; };

        await hub.SimulateReconnectedAsync("conn");
        await Task.Delay(50);

        fired.Should().BeFalse("OnReconnected must NOT fire when there is no registration message");
    }

    [Fact]
    public async Task Reconnected_SubscriberThrows_ExceptionIsSwallowed()
    {
        var (manager, hub) = CreateManager();
        manager.UpdateRegistration(DefaultRegistration);
        manager.OnReconnected += () => throw new InvalidOperationException("subscriber boom");

        var act = async () =>
        {
            await hub.SimulateReconnectedAsync("conn");
            await Task.Delay(100);
        };

        await act.Should().NotThrowAsync("OnReconnected subscriber exceptions must be swallowed");
    }

    // ── UpdateCurrentStep — volatile write-read safety ────────────────────

    [Fact]
    public void UpdateCurrentStep_MultipleSteps_NoException()
    {
        var (manager, _) = CreateManager();

        foreach (var step in Enum.GetValues<PipelineStep>())
            manager.UpdateCurrentStep(step);
        manager.UpdateCurrentStep(null);

        manager.Should().NotBeNull("volatile writes across all step values must not throw");
    }

    // ── DisposeAsync idempotency ──────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Idempotent_SecondCallDoesNotThrow()
    {
        var (manager, _) = CreateManager();
        await manager.DisposeAsync();

        var act = async () => await manager.DisposeAsync();
        await act.Should().NotThrowAsync("DisposeAsync must be idempotent");
    }

    // ── B5: StopApplication on exhausted reconnection ─────────────────────

    [Fact]
    public async Task HandleTerminalClosed_WithLifetime_AllAttemptsExhausted_CallsStopApplication()
    {
        var factory = new FakeHubConnectionManagerFactory(() =>
            new FakeHubConnectionManager { StartException = new InvalidOperationException("cannot connect") });
        var hub = new FakeHubConnectionManager();

        var stopCalled = false;
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.StopApplication()).Callback(() => stopCalled = true);

        var manager = new AgentConnectionManager(
            hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>(), lifetimeMock.Object);

        await manager.HandleTerminalClosedAsync(null, maxAttempts: 1);

        stopCalled.Should().BeTrue("StopApplication must be called when all reconnection attempts are exhausted");
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task HandleTerminalClosed_EachFailedAttempt_DisposesCreatedManager()
    {
        var disposedManagers = new List<FakeHubConnectionManager>();
        var factory = new FakeHubConnectionManagerFactory(() =>
        {
            var m = new FakeHubConnectionManager { StartException = new InvalidOperationException("fail") };
            disposedManagers.Add(m);
            return m;
        });

        var hub = new FakeHubConnectionManager();
        var manager = new AgentConnectionManager(
            hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());

        await manager.HandleTerminalClosedAsync(null, maxAttempts: 3);

        disposedManagers.Should().HaveCount(3, "one manager per attempt");
        disposedManagers.Should().AllSatisfy(m =>
            m.DisposeCallCount.Should().Be(1, "each failed manager must be disposed exactly once"));

        await manager.DisposeAsync();
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
}
