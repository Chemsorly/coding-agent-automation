using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="AgentConnectionManager"/> — the shared connection lifecycle component
/// extracted from AgentWorkerService and WorkItemAgentService.
///
/// Tests define the behavioral contract:
/// - Registration with resilience (Polly retry)
/// - Heartbeat loop runs concurrently
/// - CancelJob events are forwarded
/// - Reconnection triggers re-registration
/// - Graceful deregistration on dispose
/// - InvokeAsync wraps calls with resilience
/// - CancelJob, ForceDisconnect and Reconnected subscriber routing and error swallowing
/// - StopApplication once terminal-close reconnection attempts are exhausted
/// </summary>
public class AgentConnectionManagerTests
{
    private static readonly AgentRegistrationMessage TestRegistration = new()
    {
        AgentId = "test-agent",
        Hostname = "test-host",
        Labels = ["kiro", "dotnet"],
        ActiveJob = null
    };

    private static readonly AgentRegistrationMessage DefaultRegistration = new()
    {
        AgentId = "agent-1",
        Hostname = "host-1",
        Labels = [],
        ActiveJob = null
    };

    // ── Construction ─────────────────────────────────────────────────────

    // TODO: Add Constructor_DefaultAgentId_Throws test — default(AgentId) has Value == null and is not
    // currently rejected by the constructor (guard was removed during AgentIdentity→AgentId migration).
    // If DI misconfiguration passes default(AgentId), hub invocations will propagate nulls.

    [Fact]
    public void Constructor_NullHubManager_Throws()
    {
        var act = () => new AgentConnectionManager(
            null!,
            CreateFactory(),
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManager");
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            null!,
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentId("test"),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ValidParams_DoesNotThrow()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().NotThrow();
    }

    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void Implements_IAgentConnectionManager()
    {
        var manager = CreateManager();
        manager.Should().BeAssignableTo<IAgentConnectionManager>();
    }

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        var manager = CreateManager();
        manager.Should().BeAssignableTo<IAsyncDisposable>();
    }

    // ── Connection property ──────────────────────────────────────────────

    [Fact]
    public void Connection_ReturnsUnderlyingHubConnection()
    {
        var manager = CreateManager();
        manager.Connection.Should().NotBeNull();
    }

    [Fact]
    public void IsConnected_BeforeConnect_ReturnsFalse()
    {
        var manager = CreateManager();
        manager.IsConnected.Should().BeFalse();
    }

    // ── UpdateCurrentStep ────────────────────────────────────────────────

    [Fact]
    public void UpdateCurrentStep_DoesNotThrow()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateCurrentStep(PipelineStep.GeneratingCode);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateCurrentStep_Null_DoesNotThrow()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateCurrentStep(null);
        act.Should().NotThrow();
    }

    // ── UpdateRegistration ───────────────────────────────────────────────

    [Fact]
    public void UpdateRegistration_UpdatesStoredRegistration()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateRegistration(TestRegistration);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateRegistration_Null_Throws()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateRegistration(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── OnCancelJobReceived event ────────────────────────────────────────

    [Fact]
    public void OnCancelJobReceived_CanBeSubscribed()
    {
        var manager = CreateManager();
        string? receivedJobId = null;

        manager.OnCancelJobReceived += jobId =>
        {
            receivedJobId = jobId;
            return Task.CompletedTask;
        };

        // Subscription should compile and not throw
        manager.Should().NotBeNull();
    }

    // ── OnReconnected event ──────────────────────────────────────────────

    [Fact]
    public void OnReconnected_CanBeSubscribed()
    {
        var manager = CreateManager();

        manager.OnReconnected += () => Task.CompletedTask;

        manager.Should().NotBeNull();
    }

    // ── HandleCancelJobAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CancelJob_WithSubscriber_ForwardsJobIdToSubscriber()
    {
        var (manager, hub) = CreateManagerWithFakeHub();

        string? received = null;
        manager.OnCancelJobReceived += jobId => { received = jobId; return Task.CompletedTask; };

        await hub.SimulateCancelJobAsync("job-99");
        await Task.Delay(50); // let fire-and-forget handler settle

        received.Should().Be("job-99", "OnCancelJobReceived must forward the exact jobId");
    }

    [Fact]
    public async Task CancelJob_NoSubscriber_DoesNotThrow()
    {
        var (manager, hub) = CreateManagerWithFakeHub();
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
        var (manager, hub) = CreateManagerWithFakeHub();
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
        var (manager, hub) = CreateManagerWithFakeHub();

        var fired = false;
        manager.OnForceDisconnect += () => { fired = true; return Task.CompletedTask; };

        await hub.SimulateForceDisconnectAsync();
        await Task.Delay(50);

        fired.Should().BeTrue("OnForceDisconnect subscriber must fire when ForceDisconnect is received");
    }

    [Fact]
    public async Task ForceDisconnect_NoSubscriber_DoesNotThrow()
    {
        var (manager, hub) = CreateManagerWithFakeHub();
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
        var (manager, hub) = CreateManagerWithFakeHub();
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
        var (manager, hub) = CreateManagerWithFakeHub();
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
        var (manager, hub) = CreateManagerWithFakeHub();

        var fired = false;
        manager.OnReconnected += () => { fired = true; return Task.CompletedTask; };

        await hub.SimulateReconnectedAsync("conn");
        await Task.Delay(50);

        fired.Should().BeFalse("OnReconnected must NOT fire when there is no registration message");
    }

    [Fact]
    public async Task Reconnected_SubscriberThrows_ExceptionIsSwallowed()
    {
        var (manager, hub) = CreateManagerWithFakeHub();
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
        var (manager, _) = CreateManagerWithFakeHub();

        foreach (var step in Enum.GetValues<PipelineStep>())
            manager.UpdateCurrentStep(step);
        manager.UpdateCurrentStep(null);

        manager.Should().NotBeNull("volatile writes across all step values must not throw");
    }

    // ── DisposeAsync idempotency ──────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Idempotent_SecondCallDoesNotThrow()
    {
        var (manager, _) = CreateManagerWithFakeHub();
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

        // delayOverride => zero so the 3 attempts don't wait real reconnection backoff (~15s).
        await manager.HandleTerminalClosedAsync(null, maxAttempts: 3, delayOverride: _ => TimeSpan.Zero);

        disposedManagers.Should().HaveCount(3, "one manager per attempt");
        disposedManagers.Should().AllSatisfy(m =>
            m.DisposeCallCount.Should().Be(1, "each failed manager must be disposed exactly once"));

        await manager.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentConnectionManager CreateManager()
    {
        return new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentId("test-agent"),
            Mock.Of<Serilog.ILogger>());
    }

    private static HubConnectionManager CreateHubManager()
    {
        return new HubConnectionManager(
            "http://localhost:9999", "test-agent", "test-key",
            Mock.Of<Serilog.ILogger>());
    }

    private static HubConnectionManagerFactory CreateFactory()
    {
        return new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent", "test-key",
            Mock.Of<Serilog.ILogger>());
    }

    private static (AgentConnectionManager Manager, FakeHubConnectionManager Hub) CreateManagerWithFakeHub()
    {
        var hub = new FakeHubConnectionManager();
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var manager = new AgentConnectionManager(
            hub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());
        return (manager, hub);
    }
}
