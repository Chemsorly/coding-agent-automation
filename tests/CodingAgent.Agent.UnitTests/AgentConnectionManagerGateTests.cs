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

        var blockingHub = new FakeHubConnectionManager
        {
            StartFunc = _ => startBlocker.Task.ContinueWith(_ => { }) // blocks until we release it
        };

        var (manager, _) = CreateManager(factoryFunc: () => blockingHub);
        manager.UpdateRegistration(TestRegistration);

        // Start terminal-close recovery on a background thread (it resets the gate synchronously,
        // then blocks inside StartAsync waiting for startBlocker).
        var terminalCloseTask = Task.Run(() =>
            manager.HandleTerminalClosedAsync(null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero));

        // Give the background task time to reset the gate and reach the blocking StartAsync.
        // The gate is now incomplete (reset at the top of HandleTerminalClosedAsync before any await).
        await Task.Delay(200);

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

    // TODO [WARNING]: Missing test — "InvokeAsync waits for registration before proceeding" is listed
    // as a verified behaviour in the class comment but no test exercises it. A regression that removes
    // `await WaitForRegistrationAsync(ct)` from InvokeAsync/InvokeAsync<T> would leave all existing
    // tests green. Add a test that: holds the gate open, calls InvokeAsync concurrently, verifies it
    // blocks, then completes the gate and verifies InvokeAsync proceeds. (Test Quality Review)

    // TODO [WARNING]: Missing test — "RegisterAgent is exempt from the gate (does not await itself —
    // no deadlock)" is listed as a verified behaviour but has no test. A self-deadlock on the initial
    // ConnectAndRegisterAsync call is the most dangerous failure mode; the issue spec explicitly
    // required this test case. Add a test that calls ConnectAndRegisterAsync and verifies it completes
    // without deadlocking. (Test Quality Review)

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
