using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.JobController.UnitTests.Reconciliation;

/// <summary>
/// Unit tests for <see cref="ReconciliationService"/> — the leader-elected wrapper around
/// <see cref="ReconciliationLoop"/>. Validates that the leader gate is respected:
/// none of the four inner reconciliation tasks must be called when this instance
/// is not the leader.
///
/// The leader-wait / linked-CTS / re-entry pattern is already tested exhaustively
/// in <c>LeaderElectedPollingServiceTests</c>; these tests focus on the integration
/// between the service shell and the inner loop.
/// </summary>
public sealed class ReconciliationServiceTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient>        _k8sClient      = new();
    private readonly DispatchServiceOptions            _options;

    public ReconciliationServiceTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace                     = "test-ns",
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds  = 120
        };

        // Default: no active jobs, no active work items
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        _workItemClient
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static LeaderElectionService MakeLeaderElection(bool isLeader, CancellationTokenSource? leaderCts = null)
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        SetLeaderState(les, isLeader, leaderCts ?? new CancellationTokenSource());
        return les;
    }

    private static void SetLeaderState(LeaderElectionService les, bool isLeader, CancellationTokenSource cts)
    {
        typeof(LeaderElectionService)
            .GetField("_isLeader", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(les, isLeader);
        typeof(LeaderElectionService)
            .GetField("_leaderCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(les, cts);
    }

    private ReconciliationService MakeService(ILeaderElectionService leaderElection)
    {
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, new PvcPool([]), _options);
        return new ReconciliationService(leaderElection, loop);
    }

    private static async Task RunExecuteForDuration(BackgroundService svc, CancellationToken stopToken)
    {
        var method = typeof(BackgroundService)
            .GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(svc, [stopToken])!;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    // ── non-leader: inner loop never called ───────────────────────────────────

    [Fact]
    public async Task WhenNotLeader_ReconciliationLoop_IsNeverCalled()
    {
        // Arrange: never becomes leader during the test
        var leaderElection = MakeLeaderElection(isLeader: false);
        var svc = MakeService(leaderElection);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act: run ExecuteAsync; it spends the entire time in the 2s leader-wait loop
        await RunExecuteForDuration(svc, cts.Token);

        // Assert: no K8s or API call was made — none of the four inner tasks were entered
        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ReconcileOnceAsync must not be called when this instance is not the leader");
        _workItemClient.Verify(
            c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "EnforceTimeoutsAsync / EnforceDispatchedTimeoutAsync must not be called when not the leader");
    }

    // ── leader: inner loop is called ──────────────────────────────────────────

    [Fact]
    public async Task WhenLeader_ReconciliationLoop_IsCalled()
    {
        // Arrange: starts as leader
        var leaderCts      = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var svc = MakeService(leaderElection);

        using var stopCts = new CancellationTokenSource();

        // Act: run until at least one reconciliation cycle fires, then stop
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
            if (calls > 0) break;
            await Task.Delay(50);
        }

        stopCts.Cancel();
        await executeTask;

        // Assert: inner loop was entered at least once
        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "ReconcileOnceAsync must be called when this instance is the leader");
    }

    // ── leadership acquired mid-run ────────────────────────────────────────────

    [Fact]
    public async Task WhenLeadershipAcquiredAfterWaiting_ReconciliationLoop_IsCalled()
    {
        // Arrange: start as non-leader
        var leaderCts      = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: false, leaderCts);
        var svc = MakeService(leaderElection);

        using var stopCts = new CancellationTokenSource();
        var executeTask   = RunExecuteForDuration(svc, stopCts.Token);

        // Confirm no calls while waiting for leadership
        await Task.Delay(150);
        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act: grant leadership
        SetLeaderState(leaderElection, isLeader: true, leaderCts);

        // Wait for at least one reconciliation cycle
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
            if (calls > 0) break;
            await Task.Delay(50);
        }

        stopCts.Cancel();
        await executeTask;

        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ── IReconciliationTrigger: early wake ────────────────────────────────────

    /// <summary>
    /// After the first natural cycle completes, calling <see cref="ReconciliationService.RequestImmediateCycle"/>
    /// must trigger a second cycle well before the 30-second poll interval expires.
    /// Uses a <see cref="TestableReconciliationService"/> subclass that overrides
    /// <see cref="LeaderElectedPollingService.PollIntervalSeconds"/> to return a very large value
    /// so the test is not timing-sensitive to the real 30s interval.
    /// </summary>
    [Fact]
    public async Task WhenRequestImmediateCycleSignalled_ReconciliationLoop_IsCalledEarlierThanPollInterval()
    {
        // Arrange: ReconciliationService with a near-infinite poll interval so only the
        // trigger signal causes the second cycle — not a natural timer expiry.
        var leaderCts      = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, new PvcPool([]), _options);
        var svc  = new TestableReconciliationService(leaderElection, loop);

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        // Wait for the first natural cycle to complete
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
            if (calls >= 1) break;
            await Task.Delay(20);
        }
        var callsAfterFirstCycle = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
        callsAfterFirstCycle.Should().BeGreaterThanOrEqualTo(1, "first cycle must have fired before we signal");

        // Act: signal early wake
        svc.RequestImmediateCycle();

        // Assert: second cycle fires within 2s (not 30,000s poll interval).
        // TODO: [WARNING] The 2-second deadline may be insufficient under CI thread-pool starvation;
        // consider increasing to 5s for robustness.
        //
        // IMPORTANT: capture triggeredCalls INSIDE the polling loop (before cancellation).
        // Capturing after stopCts.Cancel() + await executeTask would allow a cycle that was
        // already mid-flight at cancellation time to satisfy the assertion even if the triggered
        // wake never fired within the 2-second window (false positive).
        int triggeredCalls = callsAfterFirstCycle; // will be updated inside the loop
        var wakeDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < wakeDeadline)
        {
            triggeredCalls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
            if (triggeredCalls >= callsAfterFirstCycle + 1) break;
            await Task.Delay(20);
        }

        stopCts.Cancel();
        await executeTask;

        triggeredCalls.Should().BeGreaterThanOrEqualTo(callsAfterFirstCycle + 1,
            "RequestImmediateCycle must wake the poll loop before the 30,000s interval expires");
    }

    /// <summary>
    /// Calling <see cref="ReconciliationService.RequestImmediateCycle"/> N times while a cycle
    /// is running collapses into at most one extra cycle (the semaphore maxCount: 1 enforces this).
    /// </summary>
    [Fact]
    public async Task WhenMultipleRequestImmediateCycleSignals_ProducesAtMostOneExtraCycle()
    {
        // Arrange: slow down each ListJobsAsync call slightly so signals can accumulate mid-flight
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken _) =>
            {
                await Task.Delay(20, CancellationToken.None); // simulate cycle taking 20ms per internal call
                return new V1JobList { Items = [] };
            });

        var leaderCts      = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, new PvcPool([]), _options);
        var svc  = new TestableReconciliationService(leaderElection, loop);

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        // Wait for the first cycle to start (give it a moment)
        await Task.Delay(50);

        // Fire 5 concurrent signals — all should collapse into at most 1 wake
        svc.RequestImmediateCycle();
        svc.RequestImmediateCycle();
        svc.RequestImmediateCycle();
        svc.RequestImmediateCycle();
        svc.RequestImmediateCycle();

        // Wait for the triggered cycle to complete, then a bit longer to confirm no extra cycles
        await Task.Delay(500);

        stopCts.Cancel();
        await executeTask;

        // Each OnPollCycleAsync calls ListJobsAsync exactly 3 times (ReconcileOnce +
        // CleanupOrphans + EnforceDispatchedTimeout). With a near-infinite poll interval
        // (30,000s), only natural start + triggered wake should fire in the test window.
        // - Minimum: 1 natural cycle = 3 calls, plus at least 1 triggered = at least 6 calls
        // - Maximum: 3 cycles × 3 calls = 9 (generous allowance for timing variation)
        // The key invariant: 5 signals must not produce 5 extra cycles (which would be ≥ 18 calls)
        var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
        calls.Should().BeGreaterThanOrEqualTo(3, "at least one complete cycle must have fired");
        // TODO: [WARNING] The upper bound of 9 (3 cycles × 3 ListJobsAsync calls) is derived from
        // an assumed internal implementation detail. If ReconciliationLoop is refactored to call
        // ListJobsAsync more times per cycle, this bound will be violated even with correct idempotency.
        // Also, the 50ms delay before firing signals may not guarantee the first cycle has started on
        // a loaded CI machine — signals could be drained on leadership entry rather than triggering a
        // wake, causing calls < 3 even with correct behaviour. Consider waiting for the first cycle to
        // complete (e.g., wait until calls >= 3) before firing signals, and measure the per-cycle
        // baseline dynamically rather than hardcoding 3.
        calls.Should().BeLessThanOrEqualTo(9,
            "5 signals must collapse into at most 1 extra cycle (semaphore maxCount: 1); " +
            "at most 3 full cycles × 3 ListJobsAsync calls each = 9 total");
    }

    /// <summary>
    /// Subclass that overrides <see cref="LeaderElectedPollingService.PollIntervalSeconds"/> to
    /// return a large value so tests don't have to wait 30 seconds for the natural timer to fire.
    /// This lets us test the trigger wake path in isolation.
    /// </summary>
    private sealed class TestableReconciliationService : ReconciliationService
    {
        // 30,000 seconds ≈ 8.3 hours — effectively infinite for tests
        protected override int PollIntervalSeconds => 30_000;

        public TestableReconciliationService(ILeaderElectionService leaderElection, ReconciliationLoop loop)
            : base(leaderElection, loop)
        {
        }
    }
}
