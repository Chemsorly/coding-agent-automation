using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for the registration gate in <see cref="AgentConnectionManager"/>.
/// Verifies that:
/// - The gate resets (becomes incomplete) at the start of reconnect/terminal-close.
/// - InvokeAsync waits for registration before proceeding.
/// - RegisterAgent is exempt from the gate (does not await itself — no deadlock).
/// - WaitForRegistrationAsync returns immediately after ConnectAndRegisterAsync succeeds.
/// - Dispose cancels waiters (no hang at shutdown).
/// </summary>
public class AgentConnectionManagerGateTests
{
    private static readonly AgentRegistrationMessage TestRegistration = new()
    {
        AgentId = "gate-agent",
        Hostname = "test-host",
        Labels = [],
        ActiveJob = null
    };

    // ── Gate starts as completed (no blocking before reconnect) ──────────

    [Fact]
    public async Task WaitForRegistrationAsync_InitialState_ReturnsImmediately()
    {
        // Gate starts completed — no waiting needed before the first reconnect
        var (manager, _) = CreateManager();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await manager.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "gate starts completed — should return immediately");
    }

    [Fact]
    public async Task WaitForRegistrationAsync_AfterGateCompleted_ReturnsImmediately()
    {
        // After reconnect, the gate resets and re-completes. WaitForRegistrationAsync
        // should return quickly after the gate is re-completed.
        var (manager, fakeHub) = CreateManager();
        manager.UpdateRegistration(TestRegistration);

        // Trigger HandleReconnectedAsync — resets gate, then TrySetResult (even on failure)
        await fakeHub.SimulateReconnectedAsync("new-conn");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await manager.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "gate is complete after HandleReconnectedAsync — should return immediately");
    }

    // ── Gate is completed by TrySetResult even on registration failure ────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterHandleReconnected_ReturnsPromptly()
    {
        var (manager, fakeHub) = CreateManager();
        manager.UpdateRegistration(TestRegistration);

        // Trigger HandleReconnectedAsync (registration will fail — InvokeAsync throws on unstarted conn)
        // but the handler sets TrySetResult even on failure so waiters are not stuck.
        await fakeHub.SimulateReconnectedAsync("new-conn");

        // WaitForRegistrationAsync should return quickly (gate completed with result/cancelled)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await manager.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "gate should be released after HandleReconnectedAsync regardless of registration outcome");
    }

    // ── Dispose cancels waiters ─────────────────────────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterDispose_ReturnsWithoutHanging()
    {
        var (manager, _) = CreateManager();

        // Dispose first — cancels the gate
        await manager.DisposeAsync();

        // WaitForRegistrationAsync should not hang
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = async () => await manager.WaitForRegistrationAsync(CancellationToken.None);

        // OperationCanceledException is acceptable (gate was cancelled) or it may return
        // immediately if the TCS was already set.
        await act.Should().NotThrowAsync<ObjectDisposedException>(
            "dispose should not cause ObjectDisposedException on WaitForRegistrationAsync");
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "should not hang indefinitely after dispose");
    }

    // ── IAgentConnectionManager contract ────────────────────────────────────

    /// <summary>
    /// Verifies the gate actually blocks callers when incomplete (i.e. during a reconnect window)
    /// and unblocks them once the gate is completed.
    /// This replaces the previously tautological test that fast-pathed because the gate starts completed.
    /// </summary>
    [Fact]
    public async Task WaitForRegistrationAsync_WhileGateIsOpen_BlocksUntilGateCompletes()
    {
        // Use a TCS to block StartAsync in the new hub manager created by HandleTerminalClosedAsync.
        // This keeps HandleTerminalClosedAsync alive (and therefore the gate incomplete) long enough
        // to start a concurrent WaitForRegistrationAsync and assert it has not yet returned.
        var startBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Signal fires when StartAsync is actually invoked — avoids a fixed-delay race.
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingHub = new FakeHubConnectionManager
        {
            StartFunc = _ =>
            {
                startEntered.TrySetResult(); // notify the test that StartAsync has begun
                return startBlocker.Task.ContinueWith(_ => { }); // blocks until we release it
            }
        };

        var (manager, _) = CreateManager(factoryFunc: () => blockingHub);
        manager.UpdateRegistration(TestRegistration);

        // Start terminal-close recovery on a background thread (it resets the gate synchronously,
        // then blocks inside StartAsync waiting for startBlocker).
        var terminalCloseTask = Task.Run(() =>
            manager.HandleTerminalClosedAsync(null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero));

        // Wait until StartAsync is actually executing (gate is reset and we are inside the blocking call).
        // This replaces the previous fixed Task.Delay(200) which was flaky on loaded CI runners.
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // WaitForRegistrationAsync should be blocking — gate is still open.
        var waitTask = manager.WaitForRegistrationAsync(CancellationToken.None);
        var completedEarly = await Task.WhenAny(waitTask, Task.Delay(150));
        completedEarly.Should().NotBe(waitTask,
            "WaitForRegistrationAsync must block while the registration gate is incomplete");

        // Release the StartAsync blocker — HandleTerminalClosedAsync will complete (gate gets TrySetResult).
        startBlocker.SetResult(true);

        // Now WaitForRegistrationAsync should unblock promptly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await waitTask;
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "WaitForRegistrationAsync must unblock promptly once the gate is completed");

        // Await the background task so it doesn't leak into subsequent tests.
        await terminalCloseTask.ContinueWith(_ => { });
    }

    // ── InvokeAsync waits for registration before proceeding ─────────────────

    /// <summary>
    /// Verifies that <see cref="AgentConnectionManager.InvokeAsync"/> waits for the registration
    /// gate before forwarding the action to the hub. A regression removing
    /// <c>await WaitForRegistrationAsync(ct)</c> from InvokeAsync would leave other gate tests green
    /// but allow hub calls to proceed during reconnection, which would be rejected by the orchestrator.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhileGateIsOpen_BlocksUntilGateCompletes()
    {
        var startBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Signal fires when StartAsync is actually invoked — avoids a fixed-delay race.
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingHub = new FakeHubConnectionManager
        {
            StartFunc = _ =>
            {
                startEntered.TrySetResult(); // notify the test that StartAsync has begun
                return startBlocker.Task.ContinueWith(_ => { }); // blocks until we release it
            }
        };

        var (manager, _) = CreateManager(factoryFunc: () => blockingHub);
        manager.UpdateRegistration(TestRegistration);

        // Start terminal-close recovery — resets the gate synchronously, then blocks in StartAsync
        var terminalCloseTask = Task.Run(() =>
            manager.HandleTerminalClosedAsync(null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero));

        // Wait until StartAsync is actually executing (gate is reset and we are inside the blocking call).
        // This replaces the previous fixed Task.Delay(200) which was flaky on loaded CI runners.
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // InvokeAsync should block — gate is still open
        var actionCalled = false;
        var invokeTask = manager.InvokeAsync(
            (conn, ct) => { actionCalled = true; return Task.CompletedTask; },
            CancellationToken.None);

        var completedEarly = await Task.WhenAny(invokeTask, Task.Delay(150));
        completedEarly.Should().NotBe(invokeTask,
            "InvokeAsync must block while the registration gate is incomplete");
        actionCalled.Should().BeFalse("the action must not be called while the gate is open");

        // Release the StartAsync blocker — reconnect completes, gate gets TrySetResult
        startBlocker.SetResult(true);

        // InvokeAsync should now unblock (action may throw since FakeHub isn't started — that's fine)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await invokeTask.ContinueWith(_ => { }); // swallow action failure
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "InvokeAsync must unblock promptly once the gate is completed");

        await terminalCloseTask.ContinueWith(_ => { });
    }

    // ── ConnectAndRegisterAsync does not self-deadlock ───────────────────────

    /// <summary>
    /// Verifies that <see cref="AgentConnectionManager.ConnectAndRegisterAsync"/> does not deadlock
    /// by awaiting the registration gate it is about to complete. The gate starts completed, so
    /// <c>InvokeAsync</c>/<c>WaitForRegistrationAsync</c> must not block the call path.
    /// </summary>
    [Fact]
    public async Task ConnectAndRegisterAsync_CompletesWithoutDeadlock()
    {
        var (manager, _) = CreateManager();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // FakeHub.StartAsync succeeds; the RegisterAgent InvokeAsync on an unstarted HubConnection
        // will throw — that is expected in tests (no real server). The key assertion is it completes
        // rather than hanging forever.
        await manager.ConnectAndRegisterAsync(TestRegistration, cts.Token)
            .ContinueWith(_ => { }); // swallow any connect/register failure

        // If we reach here without the CTS firing, there was no deadlock
        cts.IsCancellationRequested.Should().BeFalse(
            "ConnectAndRegisterAsync must not deadlock (must not await the gate it completes)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (AgentConnectionManager Manager, FakeHubConnectionManager FakeHub) CreateManager(
        Func<IHubConnectionManager>? factoryFunc = null)
    {
        var fakeHub = new FakeHubConnectionManager();
        var factory = new FakeHubConnectionManagerFactory(factoryFunc ?? (() => fakeHub));

        var manager = new AgentConnectionManager(
            fakeHub,
            factory,
            new AgentId("gate-agent"),
            Mock.Of<Serilog.ILogger>());

        return (manager, fakeHub);
    }
}
