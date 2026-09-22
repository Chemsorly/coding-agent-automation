using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConnectionReconnectCoordinator"/> in isolation.
/// Verifies:
/// - Registration gate lifecycle (initial state, reset, complete, cancel, blocking behaviour)
/// - SafeDisposeAsync (null manager, exception suppression)
/// - HandleTerminalClosedAsync (success, exhaustion, cancellation, already-disposed null guard, CAS race)
/// - DisposeAsync (gate cancellation, exactly-once hub disposal)
/// </summary>
// TODO [WARNING]: Missing test — no test verifies that _registerAgent is called with the *newly created*
// manager during HandleTerminalClosedAsync. The existing success tests use a default registerAgent
// that accepts any manager. A regression passing the old manager or null to registerAgent would leave
// all coordinator tests green. Add a test that captures the actual manager passed to registerAgent and
// asserts it is BeSameAs(newHub).
// (ConnectionReconnectCoordinatorTests.cs:1 — TestQualityReviewer review)
//
// TODO [WARNING]: Missing test — HandleTerminalClosedAsync when _registerAgent throws is not covered.
// There is a test for StartAsync failure (MultipleAttempts_EachCreatesAndDisposesNewManager) but none
// that makes _registerAgent throw and asserts (a) the failed manager is disposed and (b) the loop
// retries up to maxAttempts. A bug in the newManager ownership-transfer guard for the registration
// failure case would not be caught by existing tests.
// (ConnectionReconnectCoordinatorTests.cs:1 — TestQualityReviewer review)
public sealed class ConnectionReconnectCoordinatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static (ConnectionReconnectCoordinator Coordinator, FakeHubConnectionManager InitialHub)
        CreateCoordinator(
            Func<IHubConnectionManager>? factoryFunc = null,
            Action<IHubConnectionManager>? wireHandlers = null,
            Func<IHubConnectionManager, CancellationToken, Task>? registerAgent = null,
            Func<Task>? afterSuccessfulReconnect = null,
            IHostApplicationLifetime? lifetime = null,
            string agentId = "test-agent")
    {
        var initialHub = new FakeHubConnectionManager();
        var factory = new FakeHubConnectionManagerFactory(factoryFunc ?? (() => new FakeHubConnectionManager()));
        var coordinator = new ConnectionReconnectCoordinator(
            initialHubManager: initialHub,
            agentId: agentId,
            factory: factory,
            logger: Mock.Of<Serilog.ILogger>(),
            lifetime: lifetime,
            wireHandlers: wireHandlers ?? (_ => { }),
            registerAgent: registerAgent ?? ((_, _) => Task.CompletedTask),
            afterSuccessfulReconnect: afterSuccessfulReconnect);
        return (coordinator, initialHub);
    }

    // ── Registration gate: initial state ────────────────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_InitialState_ReturnsImmediately()
    {
        var (coordinator, _) = CreateCoordinator();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "gate starts completed — should return immediately without waiting");
    }

    // ── Registration gate: reset blocks, complete unblocks ────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterReset_BlocksUntilComplete()
    {
        var (coordinator, _) = CreateCoordinator();

        coordinator.ResetRegistrationGate(); // gate is now incomplete

        var waitTask = coordinator.WaitForRegistrationAsync(CancellationToken.None);
        var completedEarly = await Task.WhenAny(waitTask, Task.Delay(150));
        completedEarly.Should().NotBe(waitTask,
            "gate is incomplete after ResetRegistrationGate — WaitForRegistrationAsync must block");

        coordinator.CompleteRegistrationGate();

        await waitTask; // must unblock
    }

    [Fact]
    public async Task WaitForRegistrationAsync_AfterCompleteGate_ReturnsImmediately()
    {
        var (coordinator, _) = CreateCoordinator();

        coordinator.ResetRegistrationGate();
        coordinator.CompleteRegistrationGate();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "gate is completed — should return immediately");
    }

    // ── Registration gate: cancel ────────────────────────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterCancelGate_DoesNotHang()
    {
        var (coordinator, _) = CreateCoordinator();

        coordinator.ResetRegistrationGate();
        coordinator.CancelRegistrationGate(CancellationToken.None);

        // Should return quickly (OperationCanceledException is swallowed by WaitWithTimeoutAsync)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.WaitForRegistrationAsync(CancellationToken.None)
            .ContinueWith(_ => { }); // swallow cancellation
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "cancelled gate should not hang");
    }

    // ── SafeDisposeAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SafeDisposeAsync_NullManager_DoesNotThrow()
    {
        var (coordinator, _) = CreateCoordinator();

        var act = async () => await coordinator.SafeDisposeAsync(null);
        await act.Should().NotThrowAsync("null manager is a no-op");
    }

    [Fact]
    public async Task SafeDisposeAsync_ManagerDisposeThrows_ExceptionSuppressed()
    {
        var (coordinator, _) = CreateCoordinator();

        var throwingHub = new FakeHubConnectionManager();
        throwingHub.DisposeException = new InvalidOperationException("dispose boom");

        var act = async () => await coordinator.SafeDisposeAsync(throwingHub);
        await act.Should().NotThrowAsync("SafeDisposeAsync must suppress disposal exceptions");
    }

    // ── HandleTerminalClosedAsync: success ───────────────────────────────────

    [Fact]
    public async Task HandleTerminalClosedAsync_SuccessfulReconnect_GateCompleted()
    {
        var (coordinator, _) = CreateCoordinator();

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        // Gate should be completed (reconnect succeeded)
        // TODO [WARNING]: The BeLessThan(200ms) assertion below is a timing heuristic — it may produce
        // spurious failures on loaded machines. Prefer asserting gate.Task.IsCompleted synchronously
        // (or calling WaitForRegistrationAsync with a pre-cancelled token and asserting no throw) rather
        // than relying on wall-clock elapsed time.
        // (ConnectionReconnectCoordinatorTests.cs:163 — TestQualityReviewer review)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(200, "gate must be complete after successful reconnect");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_SuccessfulReconnect_InvokesAfterReconnectCallback()
    {
        var callbackFired = false;
        var (coordinator, _) = CreateCoordinator(afterSuccessfulReconnect: () =>
        {
            callbackFired = true;
            return Task.CompletedTask;
        });

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        callbackFired.Should().BeTrue("afterSuccessfulReconnect must be invoked on successful reconnect");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_SuccessfulReconnect_WiresHandlersOnNewManager()
    {
        IHubConnectionManager? wiredManager = null;
        var newHub = new FakeHubConnectionManager();

        var (coordinator, _) = CreateCoordinator(
            factoryFunc: () => newHub,
            wireHandlers: mgr => wiredManager = mgr);

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        wiredManager.Should().BeSameAs(newHub, "wireHandlers must be called on the newly created manager");
    }

    // ── HandleTerminalClosedAsync: exhaustion ───────────────────────────────

    [Fact]
    public async Task HandleTerminalClosedAsync_AllAttemptsExhausted_CallsStopApplication()
    {
        var stopCalled = false;
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.StopApplication()).Callback(() => stopCalled = true);

        var (coordinator, _) = CreateCoordinator(
            factoryFunc: () => new FakeHubConnectionManager { StartException = new InvalidOperationException("fail") },
            lifetime: lifetimeMock.Object);

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        stopCalled.Should().BeTrue("StopApplication must be called after all reconnection attempts fail");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_AllAttemptsExhausted_GateCompleted()
    {
        var (coordinator, _) = CreateCoordinator(
            factoryFunc: () => new FakeHubConnectionManager { StartException = new InvalidOperationException("fail") });

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        // Gate must be completed so callers don't hang
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(200, "gate must be released even after exhaustion");
    }

    [Fact]
    public async Task HandleTerminalClosedAsync_MultipleAttempts_EachCreatesAndDisposesNewManager()
    {
        var createdManagers = new List<FakeHubConnectionManager>();
        var (coordinator, _) = CreateCoordinator(
            factoryFunc: () =>
            {
                var m = new FakeHubConnectionManager { StartException = new InvalidOperationException("fail") };
                createdManagers.Add(m);
                return m;
            });

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 3, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        createdManagers.Should().HaveCount(3, "one manager created per attempt");
        createdManagers.Should().AllSatisfy(m =>
            m.DisposeCallCount.Should().Be(1, "each failed manager must be disposed exactly once"));
    }

    // ── HandleTerminalClosedAsync: cancellation ──────────────────────────────

    [Fact]
    public async Task HandleTerminalClosedAsync_CancellationExitsEarly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var stopCalled = false;
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.StopApplication()).Callback(() => stopCalled = true);

        var (coordinator, _) = CreateCoordinator(lifetime: lifetimeMock.Object);

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 10, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: cts.Token);

        stopCalled.Should().BeFalse("cancellation before attempts — StopApplication must not be called");
    }

    // ── HandleTerminalClosedAsync: null guard (already disposed) ─────────────

    [Fact]
    public async Task HandleTerminalClosedAsync_AlreadyDisposed_ExitsImmediately()
    {
        var factoryCalls = 0;
        var (coordinator, _) = CreateCoordinator(
            factoryFunc: () => { factoryCalls++; return new FakeHubConnectionManager(); });

        await coordinator.DisposeAsync();

        await coordinator.HandleTerminalClosedAsync(
            null, maxAttempts: 5, delayOverride: _ => TimeSpan.Zero,
            appStoppingToken: CancellationToken.None);

        factoryCalls.Should().Be(0, "disposed coordinator (_hubManager == null) must not create new managers");
    }

    // ── HandleTerminalClosedAsync: CAS prevents double-ownership ─────────────

    [Fact]
    public async Task HandleTerminalClosedAsync_ConcurrentDispose_CASPreventsDoubleOwnership()
    {
        // Use a blocker so the reconnect loop reaches the CAS point and then we dispose concurrently
        var startBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // TODO [WARNING]: The Task.Delay(200) below is a timing heuristic to ensure the background
        // reconnect task has reached StartAsync before DisposeAsync is called. On a loaded machine
        // the background task may not have reached the blocking point yet, making the test scenario
        // invalid. Replace the fixed delay with a synchronization primitive (e.g. a second
        // TaskCompletionSource signalled inside StartFunc when it is entered) to guarantee ordering.
        // (ConnectionReconnectCoordinatorTests.cs:285 — TestQualityReviewer review)
        var blockingHub = new FakeHubConnectionManager
        {
            StartFunc = _ => startBlocker.Task.ContinueWith(_ => { })
        };

        var (coordinator, _) = CreateCoordinator(factoryFunc: () => blockingHub);

        // Start terminal-close recovery on a background thread
        var terminalCloseTask = Task.Run(() =>
            coordinator.HandleTerminalClosedAsync(null, maxAttempts: 1,
                delayOverride: _ => TimeSpan.Zero,
                appStoppingToken: CancellationToken.None));

        // Let loop reach StartAsync
        await Task.Delay(200);

        // Dispose concurrently — sets _hubManager to null, invalidating the CAS
        await coordinator.DisposeAsync();

        // Unblock the reconnect StartAsync — CAS should fail; orphan manager should be disposed
        startBlocker.SetResult(true);

        // Wait for terminal close to complete — must not throw
        var act = async () => await terminalCloseTask;
        await act.Should().NotThrowAsync("CAS failure path must not throw");

        // The blockingHub (orphaned newManager) must have been disposed by SafeDisposeAsync
        blockingHub.DisposeCallCount.Should().Be(1,
            "orphaned manager after CAS failure must be disposed exactly once");
    }

    // ── DisposeAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CancelsRegistrationGate()
    {
        var (coordinator, _) = CreateCoordinator();

        coordinator.ResetRegistrationGate(); // gate is now incomplete

        await coordinator.DisposeAsync();

        // WaitForRegistrationAsync should not hang after dispose
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.WaitForRegistrationAsync(CancellationToken.None)
            .ContinueWith(_ => { }); // swallow cancellation
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "DisposeAsync must cancel the gate");
    }

    [Fact]
    public async Task DisposeAsync_DisposesInitialHub()
    {
        var (coordinator, initialHub) = CreateCoordinator();

        await coordinator.DisposeAsync();

        initialHub.DisposeCallCount.Should().Be(1, "initial hub must be disposed exactly once");
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DisposesHubOnce()
    {
        var (coordinator, initialHub) = CreateCoordinator();

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        initialHub.DisposeCallCount.Should().Be(1,
            "Interlocked.Exchange guarantees exactly-once disposal even on double DisposeAsync");
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var (coordinator, _) = CreateCoordinator();
        var act = async () => await coordinator.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // ── CurrentManager / IsConnected / Connection ───────────────────────────

    [Fact]
    public void CurrentManager_BeforeDispose_ReturnsInitialHub()
    {
        var (coordinator, initialHub) = CreateCoordinator();
        coordinator.CurrentManager.Should().BeSameAs(initialHub);
    }

    [Fact]
    public async Task CurrentManager_AfterDispose_ReturnsNull()
    {
        var (coordinator, _) = CreateCoordinator();
        await coordinator.DisposeAsync();
        coordinator.CurrentManager.Should().BeNull();
    }

    [Fact]
    public async Task Connection_AfterDispose_ThrowsObjectDisposedException()
    {
        var (coordinator, _) = CreateCoordinator();
        await coordinator.DisposeAsync();

        var act = () => _ = coordinator.Connection;
        act.Should().Throw<ObjectDisposedException>();
    }
}
