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
/// TODO: [WARNING] No test coverage for DispatchService.RunLeadershipTermAsync override behavior.
/// DispatchService is the only subclass that overrides RunLeadershipTermAsync to reset
/// _startupValidationRun before delegating to base. This is a new behavioral contract introduced
/// by issue #1758 and is not exercised end-to-end by any test in this class or in
/// DispatchServiceLifecycleTests/DispatchServiceStartupValidationTests. Add a test that drives
/// ExecuteAsync through a full leadership loss/re-acquisition cycle and asserts that startup
/// validation (_agentProfileStore.LoadAgentProfilesAsync) is called again on the second tenure.
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

        // Wait a bit — should NOT have called OnPollCycleAsync yet
        await Task.Delay(200);
        service.PollCycleCount.Should().Be(0, "should not poll before leadership is acquired");

        // Now grant leadership
        SetLeaderState(leaderElection, isLeader: true, new CancellationTokenSource());
        await Task.Delay(3000); // Wait for leader wait loop (2s) + one poll cycle

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

        // Act: start
        var executeTask = InvokeExecuteAsync(service, hostCts.Token);
        await Task.Delay(200); // Let it enter poll loop

        var countBeforeLoss = service.PollCycleCount;
        countBeforeLoss.Should().BeGreaterThan(0);

        // Lose leadership
        leaderCts.Cancel();
        SetLeaderState(leaderElection, isLeader: false, new CancellationTokenSource());
        await Task.Delay(500);

        var countAfterLoss = service.PollCycleCount;

        // Grant leadership again
        var newLeaderCts = new CancellationTokenSource();
        SetLeaderState(leaderElection, isLeader: true, newLeaderCts);
        await Task.Delay(3000); // 2s wait + poll

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
        await Task.Delay(200);

        hostCts.Cancel();

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
        await Task.Delay(500);

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
        await Task.Delay(3500); // Wait for a few cycles (~1s interval)

        service.PollCycleCount.Should().BeGreaterThanOrEqualTo(3,
            "should keep calling OnPollCycleAsync even after exceptions");

        hostCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    // ── Test helpers ────────────────────────────────────────────────────

    private static async Task InvokeExecuteAsync(LeaderElectedPollingService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [stoppingToken])!;
        await task;
    }

    private static async Task WaitForTaskCompletion(Task task)
    {
        try { await task; }
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

        protected override string ServiceName => "TestOverrideService";
        protected override int PollIntervalSeconds => 1;

        public TestOverrideService(ILeaderElectionService leaderElection) : base(leaderElection) { }

        protected override async Task RunLeadershipTermAsync(CancellationToken ct)
        {
            RunLeadershipTermCalled = true;
            // Simulate a long-running leadership term that responds to cancellation
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
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
