using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
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

    // ── InvokeAsync<T> — generic overload propagates return value ────────────

    /// <summary>
    /// Verifies that InvokeAsync&lt;T&gt; captures the value produced by the action (via the
    /// <c>T result = default!;</c> local assigned inside the Polly ExecuteAsync lambda) and
    /// returns it to the caller.  The non-generic overload is already exercised in gate tests;
    /// this test targets the distinct result-capture path in the generic overload (L163–175).
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Generic_PropagatesReturnValue()
    {
        // The action ignores the HubConnection argument, so the default FakeHubConnectionManager
        // (unstarted real HubConnection) is safe: InvokeCoreAsync is never triggered.
        var (manager, _) = CreateManagerWithFakeHub();

        var result = await manager.InvokeAsync<string>(
            (conn, ct) => Task.FromResult("sentinel-42"),
            CancellationToken.None);

        result.Should().Be("sentinel-42",
            "InvokeAsync<T> must return the value produced by the action");
    }

    // ── StopHeartbeatAsync — concurrent callers do not throw ObjectDisposedException ──

    /// <summary>
    /// Verifies that calling StopHeartbeatAsync twice concurrently (via DisposeAsync and
    /// ForceDisconnect — both paths that call StopHeartbeatAsync internally) does not throw
    /// ObjectDisposedException.  The Interlocked.Exchange in StopHeartbeatAsync ensures only
    /// one caller claims the CancellationTokenSource; the second caller receives null and exits
    /// without touching the already-cancelled/disposed CTS.
    ///
    /// ConnectAndRegisterAsync must succeed to set _heartbeatCts — requires RecordingHubConnection
    /// so the RegisterAgent hub invocation does not throw on an unstarted connection.
    /// Note: RecordingHubConnection.InvokeCoreAsync always returns success (null), so the Polly
    /// _signalRPipeline stays on the fast path and TestRetryDelayOverride is not needed here.
    ///
    /// TODO: True interleaving within Interlocked.Exchange is not deterministic from test code.
    /// A regression replacing Interlocked.Exchange with a plain read-then-null-assign could still
    /// pass this test if the OS scheduler never interleaves the two Task.Run threads inside
    /// StopHeartbeatAsync.  Fully deterministic coverage would require a synchronization hook
    /// (e.g. a ManualResetEventSlim) injected into the production path, which would require a
    /// production code change.  This test is best understood as a smoke/no-throw guard.
    /// </summary>
    [Fact]
    public async Task StopHeartbeatAsync_CalledTwiceConcurrently_DoesNotThrowObjectDisposedException()
    {
        var recording = new RecordingHubConnection();
        var fakeHub = new FakeHubConnectionManager(recording);
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var manager = new AgentConnectionManager(
            fakeHub, factory, new AgentId("agent-1"),
            Mock.Of<Serilog.ILogger>());

        // ConnectAndRegisterAsync sets _heartbeatCts and starts _heartbeatTask (30 s timer).
        // The heartbeat task completes within milliseconds once the CTS is cancelled.
        await manager.ConnectAndRegisterAsync(DefaultRegistration, CancellationToken.None);

        // Both Task.Run wrappers give each caller a separate thread-pool thread, maximising
        // the chance of concurrent execution inside StopHeartbeatAsync.
        // DisposeAsync and SimulateForceDisconnectAsync both call StopHeartbeatAsync internally.
        var t1 = Task.Run(() => manager.DisposeAsync().AsTask());
        var t2 = Task.Run(() => fakeHub.SimulateForceDisconnectAsync());

        var act = async () => await Task.WhenAll(t1, t2);
        await act.Should().NotThrowAsync<ObjectDisposedException>(
            "Interlocked.Exchange must prevent the second caller from double-cancelling the CTS");
    }

    // ── RunHeartbeatLoopAsync — tick, transient exception, cancellation ───────

    /// <summary>
    /// AC#1a — Verifies that RunHeartbeatLoopAsync sends a heartbeat on each timer tick and
    /// populates it with the correct AgentId, CurrentStep, and a non-negative MemoryUsageMb.
    ///
    /// The loop is invoked directly (not via ConnectAndRegisterAsync) to avoid starting the
    /// background 30 s loop stored in _heartbeatTask. _coordinator.CurrentManager is already set
    /// at construction, so the loop can read it without ConnectAndRegisterAsync being called first.
    /// The directly-invoked task is NOT stored in _heartbeatTask; it must be explicitly awaited.
    ///
    /// RecordingHubConnection is required: the loop calls
    ///   await manager.Connection.InvokeAsync(HubMethodNames.Heartbeat, heartbeat, ct)
    /// which routes to RecordingHubConnection.InvokeCoreAsync (records and returns null).
    ///
    /// Note: RunHeartbeatLoopAsync is internal (with InternalsVisibleTo for this project) solely
    /// to allow injecting a short interval for tests. The production call site passes no interval
    /// (30 s default preserved). See the PR description for the AC#4 deviation rationale.
    /// </summary>
    [Fact]
    public async Task RunHeartbeatLoopAsync_OnTick_SendsHeartbeatWithCorrectParameters()
    {
        var recording = new RecordingHubConnection();
        var fakeHub = new FakeHubConnectionManager(recording);
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var manager = new AgentConnectionManager(
            fakeHub, factory, new AgentId("test-agent"),
            Mock.Of<Serilog.ILogger>());
        manager.UpdateCurrentStep(PipelineStep.GeneratingCode);

        using var cts = new CancellationTokenSource();
        // Direct call with a 50 ms tick period so assertions do not wait 30 s.
        // This task is NOT stored in _heartbeatTask; DisposeAsync does not await it — we must.
        var loopTask = manager.RunHeartbeatLoopAsync(cts.Token, TimeSpan.FromMilliseconds(50));
        await Task.Delay(200); // allow ≥2 ticks at 50 ms

        await cts.CancelAsync();
        await loopTask; // required: not awaited by DisposeAsync

        var heartbeats = recording.Invocations
            .Where(i => i.MethodName == HubMethodNames.Heartbeat)
            .ToList();

        heartbeats.Should().NotBeEmpty("at least one heartbeat must be sent per timer tick");
        var msg = (HeartbeatMessage)heartbeats[0].Args[0]!; // Args[0] is object? — explicit cast
        msg.AgentId.Value.Should().Be("test-agent");
        msg.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        msg.MemoryUsageMb.Should().BeGreaterThanOrEqualTo(0,
            "MemoryUsageMb is a live process metric — any non-negative value is valid");
    }

    /// <summary>
    /// AC#1b — Verifies that a transient exception on one tick does not stop the heartbeat loop:
    /// the inner catch logs a warning and the loop continues, sending heartbeats on subsequent ticks.
    ///
    /// InvokeExceptionQueue is used to throw on the first heartbeat call only; subsequent calls
    /// return null (queue drained, InvokeException is null).
    ///
    /// The assertion verifies loop continuation (≥2 invocations recorded) rather than the warning
    /// log itself, because Serilog's generic Warning overloads are fragile under Moq verification.
    /// The AC states "loop continues after a warning log" — the continuation is the verifiable part.
    /// </summary>
    [Fact]
    public async Task RunHeartbeatLoopAsync_OnTransientException_LoopContinuesOnNextTick()
    {
        var recording = new RecordingHubConnection();
        // First heartbeat throws a transient exception; subsequent ticks succeed (queue drained).
        recording.InvokeExceptionQueue.Enqueue(new IOException("transient network glitch"));

        var fakeHub = new FakeHubConnectionManager(recording);
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var manager = new AgentConnectionManager(
            fakeHub, factory, new AgentId("test-agent"),
            Mock.Of<Serilog.ILogger>());

        using var cts = new CancellationTokenSource();
        var loopTask = manager.RunHeartbeatLoopAsync(cts.Token, TimeSpan.FromMilliseconds(50));
        await Task.Delay(300); // allow ≥3 ticks: first fails, subsequent succeed

        await cts.CancelAsync();
        await loopTask;

        var heartbeatCalls = recording.Invocations
            .Where(i => i.MethodName == HubMethodNames.Heartbeat)
            .ToList();
        heartbeatCalls.Should().HaveCountGreaterThanOrEqualTo(2,
            "loop must continue after a transient exception — at least one successful tick must follow the failed one");
    }

    /// <summary>
    /// Verifies that cancelling the token causes RunHeartbeatLoopAsync to exit cleanly
    /// without propagating OperationCanceledException to the caller.
    /// The outer catch in the loop body swallows the expected cancellation exception.
    /// </summary>
    [Fact]
    public async Task RunHeartbeatLoopAsync_OnCancellation_ExitsCleanlyWithoutException()
    {
        var fakeHub = new FakeHubConnectionManager(new RecordingHubConnection());
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var manager = new AgentConnectionManager(
            fakeHub, factory, new AgentId("test-agent"),
            Mock.Of<Serilog.ILogger>());

        using var cts = new CancellationTokenSource();
        var loopTask = manager.RunHeartbeatLoopAsync(cts.Token, TimeSpan.FromMilliseconds(50));

        await cts.CancelAsync();
        var act = async () => await loopTask;
        await act.Should().NotThrowAsync<OperationCanceledException>(
            "cancellation is handled internally and must not propagate to the caller");
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
