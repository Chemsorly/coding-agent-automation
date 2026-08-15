using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="LeaderElectedPollingService"/> base class.
/// Validates: leader-wait pattern, linked CTS, poll loop, leadership loss re-entry.
/// </summary>
/// <remarks>
/// TODO: No test coverage for ReconciliationService's RunLeadershipTermAsync override behavior.
/// The refactoring removed the explicit `await linked.CancelAsync()` when one of watch/poll tasks
/// completes without the CT being cancelled. A scenario where watchTask faults while pollTask is
/// in a long Task.Delay would now wait until the delay completes rather than being cancelled
/// immediately. This behavioral change is untested.
/// </remarks>
[Trait("Feature", "LeaderElectedPollingService")]
public class LeaderElectedPollingServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WaitsForLeadership_BeforeCallingOnPollCycleAsync()
    {
        // Arrange: not leader initially
        var leaderElection = CreateLeaderElection(isLeader: false);
        var service = new TestPollingService(leaderElection, pollIntervalSeconds: 1);
        var cts = new CancellationTokenSource();

        // Act: start ExecuteAsync
        var executeTask = InvokeExecuteAsync(service, cts.Token);

        // Count is synchronously 0 — ExecuteAsync is waiting in the 2s leader-wait loop
        service.PollCycleCount.Should().Be(0, "should not poll before leadership is acquired");

        // Now grant leadership
        SetLeaderState(leaderElection, isLeader: true, new CancellationTokenSource());

        // Poll until the first cycle fires. Leader-wait loop checks every 2s, so this
        // resolves within ≤2s. Deadline of 10s is a generous safety bound for CI.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.PollCycleCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        service.PollCycleCount.Should().BeGreaterThan(0, "should poll after leadership is acquired");

        // Cleanup
        cts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    [Fact]
    public async Task ExecuteAsync_LeadershipLost_ReEntersWaitLoop()
    {
        // Arrange: start as leader
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElection(isLeader: true, leaderCts);
        var service = new TestPollingService(leaderElection, pollIntervalSeconds: 1);
        var hostCts = new CancellationTokenSource();

        // TODO: WARNING — This test uses TestPollingService, which routes the OCE through the inner
        // catch inside RunLeadershipTermAsync's default poll loop, not through the outer
        // catch (OperationCanceledException) in ExecuteAsync that was changed by the fix.
        // A complementary test using TestOverrideService (which lets the OCE reach the outer catch
        // directly) would more precisely verify that the fix does not regress the pure leadership-loss
        // re-entry path via the exact catch block that was modified. See Issue #2027 review findings.

        // Act: start, then wait for the first poll to confirm we entered the loop
        var executeTask = InvokeExecuteAsync(service, hostCts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.PollCycleCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var countBeforeLoss = service.PollCycleCount;
        countBeforeLoss.Should().BeGreaterThan(0);

        // Lose leadership
        leaderCts.Cancel();
        SetLeaderState(leaderElection, isLeader: false, new CancellationTokenSource());
        // Brief fixed delay — just enough for the cancellation to propagate through the loop
        await Task.Delay(100);

        var countAfterLoss = service.PollCycleCount;

        // Grant leadership again
        var newLeaderCts = new CancellationTokenSource();
        SetLeaderState(leaderElection, isLeader: true, newLeaderCts);

        // Poll until a new cycle fires after re-acquisition (≤2s leader-wait + 1s poll)
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.PollCycleCount <= countAfterLoss && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        service.PollCycleCount.Should().BeGreaterThan(countAfterLoss,
            "should resume polling after leadership reacquired");

        // Cleanup
        hostCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    [Fact]
    public async Task ExecuteAsync_HostStopping_ExitsGracefully()
    {
        var leaderElection = CreateLeaderElection(isLeader: true, new CancellationTokenSource());
        var service = new TestPollingService(leaderElection, pollIntervalSeconds: 1);
        var hostCts = new CancellationTokenSource();

        var executeTask = InvokeExecuteAsync(service, hostCts.Token);

        // Wait for at least one poll to confirm the service entered the loop
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.PollCycleCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        hostCts.Cancel();

        // Service should exit promptly after cancellation — WhenAny is the safety bound
        var completed = await Task.WhenAny(executeTask, Task.Delay(5000));
        completed.Should().Be(executeTask, "ExecuteAsync should exit promptly on host stop");
    }

    [Fact]
    public async Task RunLeadershipTermAsync_Override_IsUsedInsteadOfDefaultPollLoop()
    {
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElection(isLeader: true, leaderCts);
        var service = new TestOverrideService(leaderElection);
        var hostCts = new CancellationTokenSource();

        var executeTask = InvokeExecuteAsync(service, hostCts.Token);

        // Wait for RunLeadershipTermAsync to be entered — event-driven via TCS
        await service.RunLeadershipTermEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.RunLeadershipTermCalled.Should().BeTrue("should call overridden RunLeadershipTermAsync");
        service.PollCycleCount.Should().Be(0, "OnPollCycleAsync should NOT be called when RunLeadershipTermAsync is overridden");

        hostCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    [Fact]
    public async Task OnPollCycleAsync_ExceptionDoesNotTerminateLoop()
    {
        var leaderElection = CreateLeaderElection(isLeader: true, new CancellationTokenSource());
        var service = new TestThrowingService(leaderElection, pollIntervalSeconds: 1, throwOnFirstNCalls: 2);
        var hostCts = new CancellationTokenSource();

        var executeTask = InvokeExecuteAsync(service, hostCts.Token);

        // Poll until ≥3 cycles complete. With 1s intervals, this takes ~2s.
        // 10s deadline is a generous bound for CI.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.PollCycleCount < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        service.PollCycleCount.Should().BeGreaterThanOrEqualTo(3,
            "should keep calling OnPollCycleAsync even after exceptions");

        hostCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    [Fact]
    public void Constructor_NullLeaderElection_ThrowsArgumentNullException()
    {
        // Act & Assert: constructing with null leaderElection must throw immediately
        var ex = Assert.Throws<ArgumentNullException>(
            () => new TestPollingService(null!, pollIntervalSeconds: 1));
        ex.ParamName.Should().Be("leaderElection");
    }

    [Fact]
    public async Task ExecuteAsync_HostStopAndLeadershipLossSimultaneous_ExitsWithoutError()
    {
        // Arrange: start as leader using TestOverrideService so we can wait for RunLeadershipTermAsync entry
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElection(isLeader: true, leaderCts);
        var service = new TestOverrideService(leaderElection);
        var hostCts = new CancellationTokenSource();

        // Obtain the raw inner ExecuteAsync Task directly — do NOT use InvokeExecuteAsync or
        // WaitForTaskCompletion, because WaitForTaskCompletion swallows OperationCanceledException
        // and would make this test tautological (passing even when the bug is present).
        // TODO: WARNING — If GetMethod returns null (e.g., due to a future .NET runtime rename),
        // the `!` null-forgiving operator suppresses the nullable warning and the subsequent Invoke
        // throws NullReferenceException rather than a descriptive test failure. Add
        // Assert.NotNull(executeMethod) before the cast to make the failure mode explicit.
        var executeMethod = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var executeTask = (Task)executeMethod!.Invoke(service, [hostCts.Token])!;

        // Wait (event-driven) until RunLeadershipTermAsync has been entered — the service is now
        // inside await Task.Delay(Timeout.Infinite, ct) and will respond to cancellation.
        // TODO: WARNING — If WaitAsync times out (e.g., service never acquires leadership due to a
        // test-environment issue), it throws TimeoutException with no context about which assertion
        // failed. Consider wrapping in a try/catch or using a polling loop with a descriptive
        // Assert failure to improve CI diagnosability.
        await service.RunLeadershipTermEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Simultaneously cancel BOTH leadership and host stop — this is the race that triggered
        // the spurious Error log before the fix.
        leaderCts.Cancel();
        hostCts.Cancel();

        // ExecuteAsync should exit promptly — 5s is a generous CI safety bound.
        var completed = await Task.WhenAny(executeTask, Task.Delay(5000));
        completed.Should().Be(executeTask, "ExecuteAsync should exit promptly on simultaneous cancellation");

        // KEY ASSERTION: the task must have run to completion, not faulted.
        // Before the fix: the OCE filter evaluates false, OCE propagates, task faults → IsCompletedSuccessfully == false
        // After the fix: OCE is caught unconditionally, stoppingToken check fires break, task completes cleanly
        // TODO: WARNING — If Task.Delay(5000) wins the WhenAny race (e.g., under heavy CI load),
        // executeTask is still running and IsCompletedSuccessfully returns false, making this
        // assertion fail with a misleading "bug is present" message instead of surfacing the
        // timeout. Consider awaiting executeTask directly with a timeout (e.g.,
        // await executeTask.WaitAsync(TimeSpan.FromSeconds(5))) before asserting the property.
        executeTask.IsCompletedSuccessfully.Should().BeTrue(
            "simultaneous host stop and leadership loss should not produce an unhandled OperationCanceledException");
    }

    // ── Test helpers ────────────────────────────────────────────────────

    private static async Task InvokeExecuteAsync(LeaderElectedPollingService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [stoppingToken])!;
        // ConfigureAwait(false) prevents capturing xUnit's single-threaded AsyncTestSyncContext.
        // Without it, continuations of the background ExecuteAsync loop get queued on xUnit's
        // context — which is already blocked awaiting the test — causing a sync-context deadlock.
        await task.ConfigureAwait(false);
    }

    private static async Task WaitForTaskCompletion(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
    }

    private static LeaderElectionService CreateLeaderElection(bool isLeader, CancellationTokenSource? cts = null)
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        SetLeaderState(les, isLeader, cts ?? new CancellationTokenSource());
        return les;
    }

    private static void SetLeaderState(LeaderElectionService les, bool isLeader, CancellationTokenSource cts)
    {
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField!.SetValue(les, isLeader);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        leaderCtsField!.SetValue(les, cts);
    }

    // ── Test doubles ────────────────────────────────────────────────────

    private sealed class TestPollingService : LeaderElectedPollingService
    {
        private readonly int _pollIntervalSeconds;
        public int PollCycleCount;

        protected override string ServiceName => "TestPollingService";
        protected override int PollIntervalSeconds => _pollIntervalSeconds;

        public TestPollingService(ILeaderElectionService leaderElection, int pollIntervalSeconds)
            : base(leaderElection)
        {
            _pollIntervalSeconds = pollIntervalSeconds;
        }

        protected override Task OnPollCycleAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref PollCycleCount);
            return Task.CompletedTask;
        }
    }

    private sealed class TestOverrideService : LeaderElectedPollingService
    {
        public bool RunLeadershipTermCalled;
        public int PollCycleCount;

        /// <summary>Fires when <see cref="RunLeadershipTermAsync"/> is entered.</summary>
        public readonly TaskCompletionSource RunLeadershipTermEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override string ServiceName => "TestOverrideService";
        protected override int PollIntervalSeconds => 1;

        public TestOverrideService(ILeaderElectionService leaderElection) : base(leaderElection) { }

        protected override async Task RunLeadershipTermAsync(CancellationToken ct)
        {
            RunLeadershipTermCalled = true;
            RunLeadershipTermEntered.TrySetResult();
            // Simulate a long-running leadership term that propagates cancellation.
            // Do NOT catch OperationCanceledException here — the OCE must escape to ExecuteAsync's
            // outer catch block, which is the code under test in ExecuteAsync_HostStopAndLeadershipLossSimultaneous_ExitsWithoutError.
            // Swallowing it here would make that test tautological: the outer catch is never reached
            // and IsCompletedSuccessfully would be true regardless of whether the fix is in place.
            await Task.Delay(Timeout.Infinite, ct);
        }

        protected override Task OnPollCycleAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref PollCycleCount);
            return Task.CompletedTask;
        }
    }

    private sealed class TestThrowingService : LeaderElectedPollingService
    {
        private readonly int _throwOnFirstNCalls;
        public int PollCycleCount;

        protected override string ServiceName => "TestThrowingService";
        protected override int PollIntervalSeconds { get; }

        public TestThrowingService(ILeaderElectionService leaderElection, int pollIntervalSeconds, int throwOnFirstNCalls)
            : base(leaderElection)
        {
            PollIntervalSeconds = pollIntervalSeconds;
            _throwOnFirstNCalls = throwOnFirstNCalls;
        }

        protected override Task OnPollCycleAsync(CancellationToken ct)
        {
            var count = Interlocked.Increment(ref PollCycleCount);
            if (count <= _throwOnFirstNCalls)
                throw new InvalidOperationException($"Simulated failure #{count}");
            return Task.CompletedTask;
        }
    }
}
