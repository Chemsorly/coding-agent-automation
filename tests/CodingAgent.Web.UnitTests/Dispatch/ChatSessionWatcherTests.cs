using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using k8s.Models;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ChatSessionWatcher"/>.
/// </summary>
public class ChatSessionWatcherTests
{
    private const string TestNamespace = "coding-agent";
    private const string TestJobName = "caa-chat-test0001";
    private const string TestAgentId = "caa-chat-test0001";
    private const string TestSelector = "dotnet,kiro";

    private static DispatchServiceOptions CreateOptions(int idleTimeoutSeconds = 3600, int gracePeriod = 1) => new()
    {
        Namespace = TestNamespace,
        KiroPvcPool = ["pvc-0"],
        OrchestratorUrl = "http://orchestrator:8080",
        AgentApiKeySecretName = "caa-secret",
        AgentServiceAccountName = "caa-agent",
        ChatIdleTimeoutSeconds = idleTimeoutSeconds,
        ChatTerminationGracePeriodSeconds = gracePeriod,
        ChatPodConnectTimeoutSeconds = 5,
        ChatJobMaxDurationSeconds = 7200
    };

    private static Mock<IKubernetesJobClient> CreateJobClientMock(V1Job? readResult = null, bool throwNotFound = false)
    {
        var mock = new Mock<IKubernetesJobClient>();
        mock.Setup(c => c.ListJobsAsync(TestNamespace, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        mock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        if (throwNotFound)
            mock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Operation returned an invalid status code 'NotFound'"));
        else
            mock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
                .ReturnsAsync(readResult ?? new V1Job { Status = new V1JobStatus { Conditions = [] } });

        return mock;
    }

    private static ChatJobDispatcher.WatcherEntry CreateEntry(
        string agentId = TestAgentId,
        string jobName = TestJobName,
        string selector = TestSelector,
        string? pvc = "pvc-0",
        CancellationTokenSource? cts = null)
    {
        cts ??= new CancellationTokenSource();
        var identity = new ChatJobDispatcher.WatcherIdentity(
            new AgentId(agentId), jobName, selector, pvc);
        return new ChatJobDispatcher.WatcherEntry(identity, DateTimeOffset.UtcNow, cts);
    }

    private static ChatSessionWatcher CreateWatcher(
        IKubernetesJobClient? jobClient = null,
        IChatHeartbeatTracker? tracker = null,
        DispatchServiceOptions? options = null)
    {
        return new ChatSessionWatcher(
            jobClient ?? CreateJobClientMock().Object,
            tracker,
            options ?? CreateOptions(),
            Mock.Of<ILogger>());
    }

    // ─── Job becomes terminal ─────────────────────────────────────────────────

    [Fact]
    public async Task WatchJob_JobBecomesTerminal_CallsCleanupCallbackAndReturns()
    {
        var jobClientMock = CreateJobClientMock(new V1Job
        {
            Status = new V1JobStatus
            {
                Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }]
            }
        });

        var watcher = CreateWatcher(jobClientMock.Object);
        using var cts = new CancellationTokenSource();
        var entry = CreateEntry(cts: cts);

        string? firstCleanupOutcome = null;
        var cleanupCallCount = 0;

        // The cleanup callback mirrors CleanupSession's CAS gate: only the first call is recorded.
        // The finally block in WatchJobUntilTerminalAsync always calls cleanup (idempotent in production
        // because CleanupSession has a Interlocked.CompareExchange gate).
        Action<string, ChatJobDispatcher.WatcherEntry, string, string> cleanup = (aid, e, sel, outcome) =>
        {
            cleanupCallCount++;
            if (cleanupCallCount == 1)
                firstCleanupOutcome = outcome;
        };

        await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => Task.CompletedTask,
            cleanup,
            cts.Token);

        cleanupCallCount.Should().BeGreaterThanOrEqualTo(1, "cleanup callback must be called at least once");
        firstCleanupOutcome.Should().Be("completed",
            "the first cleanup call (from the terminal exit path) must have outcome 'completed'");
    }

    [Fact]
    public async Task WatchJob_JobNotFound_TreatedAsTerminalCallsCleanup()
    {
        var jobClientMock = CreateJobClientMock(throwNotFound: true);
        var watcher = CreateWatcher(jobClientMock.Object);
        using var cts = new CancellationTokenSource();
        var entry = CreateEntry(cts: cts);

        var cleanupCallCount = 0;
        string? firstCleanupOutcome = null;

        await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => Task.CompletedTask,
            (_, _, _, outcome) =>
            {
                cleanupCallCount++;
                if (cleanupCallCount == 1) firstCleanupOutcome = outcome;
            },
            cts.Token);

        cleanupCallCount.Should().BeGreaterThanOrEqualTo(1, "404 job must be treated as terminal");
        firstCleanupOutcome.Should().Be("completed");
    }

    // ─── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WatchJob_CancellationRequested_CallsCleanupCallbackWithShutdownOutcome()
    {
        // Non-terminal job — watcher would loop forever; cancel to exit
        var jobClientMock = CreateJobClientMock();
        var watcher = CreateWatcher(jobClientMock.Object, options: CreateOptions(idleTimeoutSeconds: 3600));
        using var cts = new CancellationTokenSource();
        var entry = CreateEntry(cts: cts);

        var cleanupCallCount = 0;
        string? firstCleanupOutcome = null;

        var watchTask = Task.Run(() => watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => Task.CompletedTask,
            (_, _, _, outcome) =>
            {
                cleanupCallCount++;
                if (cleanupCallCount == 1) firstCleanupOutcome = outcome;
            },
            cts.Token));

        // Give the watcher a moment to start, then cancel
        await Task.Delay(100);
        await cts.CancelAsync();

        await watchTask.WaitAsync(TimeSpan.FromSeconds(5));

        cleanupCallCount.Should().BeGreaterThanOrEqualTo(1, "cleanup must be called on cancellation");
        firstCleanupOutcome.Should().Be("shutdown");
    }

    // ─── Idle-kill: local ticks ───────────────────────────────────────────────

    [Fact]
    public async Task WatchJob_IdleTimeout_LocalTicks_TriggersKillViaTerminateCallback()
    {
        var jobClientMock = CreateJobClientMock();
        var watcher = CreateWatcher(
            jobClientMock.Object,
            tracker: null, // no Redis — local ticks are authoritative
            options: CreateOptions(idleTimeoutSeconds: 2));
        using var cts = new CancellationTokenSource();

        var entry = CreateEntry(cts: cts);
        // Simulate stale ticks by setting LastClientHeartbeatTicks to a value 10s ago
        System.Threading.Interlocked.Exchange(
            ref entry.LastClientHeartbeatTicks,
            DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(10)).UtcTicks);

        var terminateCalled = false;
        var cleanupCallCount = 0;

        await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (id, ct) => { terminateCalled = true; return Task.CompletedTask; },
            (_, _, _, _) => { cleanupCallCount++; },
            cts.Token);

        terminateCalled.Should().BeTrue("terminate callback must be invoked on idle-kill");
        cleanupCallCount.Should().BeGreaterThanOrEqualTo(1, "cleanup callback must be called after idle-kill");
        // TODO [WARNING]: This test only verifies that the terminate callback fires. No test covers
        // the real production interaction where terminateCallback = TerminateChatSessionAsync, which
        // awaits entry.WatcherTask — the watcher's own task — causing a self-await / grace-period stall
        // on every idle-kill. The most impactful correctness characteristic of the idle-kill path is
        // therefore not covered. Add an integration-level test on ChatJobDispatcher that drives a real
        // idle-kill and asserts observable outcomes (force-delete, session removed, PodForceTerminations
        // incremented). See review finding: TestQualityReviewer WARNING @ ChatSessionWatcherTests.cs:203.
    }

    // ─── Idle-kill: Redis fault skips kill ────────────────────────────────────

    [Fact]
    public async Task WatchJob_RedisFault_SkipsIdleKillForCycle()
    {
        var redisMock = new Mock<IChatHeartbeatTracker>();
        // Tracker returns (Available=false, null) — simulating a Redis fault.
        // ChatHeartbeatTracker.TryGetRedisHeartbeatAsync catches Redis exceptions and returns (false, null).
        // The watcher receives this and skips idle-kill for the cycle.
        redisMock.Setup(r => r.TryGetRedisHeartbeatAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((false, (DateTimeOffset?)null));

        // With Redis faulting, the watcher skips idle-kill and loops.
        // Cancel after a short delay to observe it never triggered the terminate callback.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var watcher = CreateWatcher(
            tracker: redisMock.Object,
            options: CreateOptions(idleTimeoutSeconds: 1));
        var entry = CreateEntry(cts: cts);
        // Make ticks stale
        System.Threading.Interlocked.Exchange(
            ref entry.LastClientHeartbeatTicks,
            DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(10)).UtcTicks);

        var terminateCalled = false;

        await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => { terminateCalled = true; return Task.CompletedTask; },
            (_, _, _, _) => { },
            cts.Token);

        terminateCalled.Should().BeFalse(
            "terminate callback must NOT be called when Redis faults (Available=false) — idle-kill must be skipped");
        // TODO [WARNING]: This test relies on wall-clock timing (500ms cancellation). If the test host
        // is under CPU load and the first watcher loop iteration hasn't run before cancellation fires,
        // the test passes vacuously — terminate was never called simply because the loop never ran.
        // Fix: verify TryGetRedisHeartbeatAsync was called ≥1 time via mock.Verify before concluding
        // idle-kill was correctly skipped. See review finding: TestQualityReviewer WARNING @ line 247.
    }

    // ─── CAS guard prevents double termination ────────────────────────────────

    [Fact]
    public async Task WatchJob_GuardFired_NoDoubleTerminationCallback()
    {
        // Pre-set Terminating=1 so the CAS guard fires immediately
        var jobClientMock = CreateJobClientMock();
        var watcher = CreateWatcher(
            jobClientMock.Object,
            tracker: null,
            options: CreateOptions(idleTimeoutSeconds: 2));
        using var cts = new CancellationTokenSource();
        var entry = CreateEntry(cts: cts);

        // Make ticks stale so idle-kill threshold is exceeded
        System.Threading.Interlocked.Exchange(
            ref entry.LastClientHeartbeatTicks,
            DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(10)).UtcTicks);

        // Pre-set Terminating=1 to simulate another path already terminating
        System.Threading.Interlocked.Exchange(ref entry.Terminating, 1);

        var terminateCallCount = 0;

        // Cancel after a short delay so the watcher exits the GuardFired loop
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            await cts.CancelAsync();
        });

        await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => { terminateCallCount++; return Task.CompletedTask; },
            (_, _, _, _) => { },
            cts.Token);

        terminateCallCount.Should().Be(0,
            "terminate callback must not be called when Terminating guard is already set");
    }

    // ─── Exception in watcher calls cleanup in finally ────────────────────────

    [Fact]
    public async Task WatchJob_UnhandledException_CallsCleanupInFinally()
    {
        // Make the job terminal immediately so the watcher tries to call cleanupCallback("completed").
        // The first call to cleanupCallback throws, which propagates to the outer catch (Exception),
        // which then calls cleanupCallback("faulted") via the finally block.
        var jobClientMock = CreateJobClientMock(new V1Job
        {
            Status = new V1JobStatus
            {
                Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }]
            }
        });

        var watcher = CreateWatcher(jobClientMock.Object);
        using var cts = new CancellationTokenSource();
        var entry = CreateEntry(cts: cts);

        var cleanupCallCount = 0;
        // First call throws (simulates a fault in a cleanup side-effect like metric recording).
        // The finally block then calls cleanup again.
        Action<string, ChatJobDispatcher.WatcherEntry, string, string> faultingCleanup = (_, _, _, _) =>
        {
            cleanupCallCount++;
            if (cleanupCallCount == 1)
                throw new InvalidOperationException("simulated fault in cleanup callback");
        };

        // The watcher should not rethrow — it logs the error and lets finally run
        var act = async () => await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => Task.CompletedTask,
            faultingCleanup,
            cts.Token);

        await act.Should().NotThrowAsync("unexpected exceptions must be caught by the watcher's fault guard");
        cleanupCallCount.Should().BeGreaterThanOrEqualTo(2,
            "cleanup must be called in finally even when the first cleanup call threw");
    }

    // ─── Transient read error retries ─────────────────────────────────────────

    [Fact]
    public async Task WatchJob_TransientReadError_RetriesWithoutExiting()
    {
        // ReadJobAsync: first call throws transient, second call returns terminal
        var jobClientMock = new Mock<IKubernetesJobClient>();
        jobClientMock.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        // Use a short idle timeout so pollInterval = Math.Min(10, Math.Max(1, 3/3)) = 1s
        // This keeps the test fast — the watcher polls every 1s and retries promptly.
        var watcher = CreateWatcher(
            jobClientMock.Object,
            options: CreateOptions(idleTimeoutSeconds: 3, gracePeriod: 1));
        using var cts = new CancellationTokenSource();
        var entry = CreateEntry(cts: cts);

        var callCount = 0;
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // Refresh heartbeat on every poll so CI slowness cannot expire the 3s idle window
                // before the second ReadJobAsync call. Without this, slow CI agents can delay the
                // watcher loop long enough that idle-kill fires first, keeping callCount at 1.
                System.Threading.Interlocked.Exchange(
                    ref entry.LastClientHeartbeatTicks,
                    DateTimeOffset.UtcNow.UtcTicks);
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("transient error");
                return Task.FromResult(new V1Job
                {
                    Status = new V1JobStatus
                    {
                        Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }]
                    }
                });
            });

        var cleanupCallCount = 0;
        string? firstCleanupOutcome = null;

        await watcher.WatchJobUntilTerminalAsync(
            TestJobName, entry,
            (_, _) => Task.CompletedTask,
            (_, _, _, outcome) =>
            {
                cleanupCallCount++;
                if (cleanupCallCount == 1) firstCleanupOutcome = outcome;
            },
            cts.Token);

        callCount.Should().BeGreaterThanOrEqualTo(2, "watcher must retry after a transient error");
        cleanupCallCount.Should().BeGreaterThanOrEqualTo(1, "cleanup must be called after the job becomes terminal");
        firstCleanupOutcome.Should().Be("completed");
    }
}
