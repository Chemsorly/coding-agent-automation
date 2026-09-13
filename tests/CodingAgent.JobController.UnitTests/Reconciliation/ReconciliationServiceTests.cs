using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using CodingAgent.JobController.Dispatch;
using CodingAgent.JobController.Reconciliation;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.JobController.UnitTests.Reconciliation;

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
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options;

    public ReconciliationServiceTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            ChatJobMaxDurationSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120
        };

        // Default: no active jobs, no active work items
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        _workItemClient
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
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
            c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "EnforceTimeoutsAsync / EnforceDispatchedTimeoutAsync must not be called when not the leader");
    }

    // ── leader: inner loop is called ──────────────────────────────────────────

    [Fact]
    public async Task WhenLeader_ReconciliationLoop_IsCalled()
    {
        // Arrange: starts as leader
        var leaderCts = new CancellationTokenSource();
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
        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: false, leaderCts);
        var svc = MakeService(leaderElection);

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

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
        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        var svc = new TestableReconciliationService(leaderElection, loop);

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

        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        var svc = new TestableReconciliationService(leaderElection, loop);

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        // Wait until the first cycle has fully completed (all 3 ListJobsAsync calls are in).
        // Polling is deterministic and CI-load-safe; a fixed Task.Delay(50) was insufficient
        // on loaded machines because the service task may not have been scheduled in time.
        var waitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (_k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync)) < 3
               && DateTime.UtcNow < waitDeadline)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        // Capture the per-cycle baseline dynamically so the upper bound does not depend on
        // a hardcoded assumption about how many ListJobsAsync calls each cycle makes.
        var callsAfterCycleOne = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));

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
        // (30,000s), only the natural start cycle + one triggered cycle should fire.
        // - Minimum: callsAfterCycleOne + 1 triggered cycle worth of calls
        // - Maximum: callsAfterCycleOne + 2 × callsAfterCycleOne (generous timing slack)
        // The key invariant: 5 signals must not produce 5 extra cycles (which would be ≥ 18 calls)
        var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
        calls.Should().BeGreaterThanOrEqualTo(callsAfterCycleOne + 1,
            "at least one triggered cycle must have fired after the signals");
        calls.Should().BeLessThanOrEqualTo(callsAfterCycleOne * 3,
            "5 signals must collapse into at most 1 extra cycle (semaphore maxCount: 1); " +
            "at most 2 full cycles beyond baseline = 3× per-cycle call count");
    }

    // ── Transient exception log-level classification ──────────────────────────

    // TODO [WARNING]: There is no test for RunSafe being invoked with a BrokenCircuitException
    // (the fourth type in IsTransientPollingException). RunSafe_WhenTaskThrowsHttpRequestException_LogsAtWarningNotError
    // covers one transient type; adding a RunSafe_WhenTaskThrowsBrokenCircuitException_LogsAtWarningNotError
    // test would confirm the full contract for the most operationally significant type (circuit-breaker
    // open state under load). See Issue #2576 review findings (TestQualityReviewer [WARNING]).

    /// <summary>
    /// Regression test for Issue #2576: an HttpRequestException thrown by OnPollCycleAsync
    /// (the outer poll-loop catch in ReconciliationService.RunLeadershipTermAsync) must log
    /// at Warning, not Error, and must not terminate the loop.
    ///
    /// This test uses a subclass that overrides OnPollCycleAsync to throw directly, so the
    /// exception reaches the outer poll-loop catch in RunLeadershipTermAsync rather than being
    /// absorbed by ReconciliationLoop's internal per-method exception handlers.
    /// </summary>
    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task RunLeadershipTerm_WhenOnPollCycleThrowsHttpRequestException_LogsAtWarningNotError()
    {
        // Arrange: capture log events via a real Serilog sink
        var sink = new CapturingSink();
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        // Use the throwing subclass to bypass ReconciliationLoop's internal exception absorption
        var throwCount = 0;
        var svc = new ThrowingOnPollCycleReconciliationService(
            leaderElection, loop,
            onCycle: () =>
            {
                if (Interlocked.Increment(ref throwCount) == 1)
                    throw new HttpRequestException("simulated transient blip");
            });

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        // Wait until the second poll cycle (recovery after the throw)
        var deadline = DateTime.UtcNow.AddSeconds(10);
        // TODO [WARNING]: throwCount is a plain int field; Interlocked.Increment writes it but this
        // spin-loop read is non-volatile. On Release builds with aggressive register allocation, the
        // JIT may cache the stale value and never observe throwCount >= 2, causing the test to spin
        // until the deadline. Fix: use Volatile.Read(ref throwCount) in the loop condition.
        // See Issue #2576 review findings (TestQualityReviewer [WARNING]).
        while (throwCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        stopCts.Cancel();
        await executeTask;

        // Assert: Warning was emitted, not Error
        sink.Events
            .Should().Contain(e =>
                e.Level == LogEventLevel.Warning &&
                e.Exception is HttpRequestException,
            "HttpRequestException must log at Warning level in the outer poll-loop catch");

        sink.Events
            .Should().NotContain(e =>
                e.Level == LogEventLevel.Error &&
                e.Exception is HttpRequestException,
            "HttpRequestException must NOT log at Error level");
    }

    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task RunLeadershipTerm_WhenOnPollCycleThrowsNonTransientException_LogsAtError()
    {
        // Arrange
        var sink = new CapturingSink();
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        var throwCount = 0;
        var svc = new ThrowingOnPollCycleReconciliationService(
            leaderElection, loop,
            onCycle: () =>
            {
                if (Interlocked.Increment(ref throwCount) == 1)
                    throw new InvalidOperationException("genuine bug");
            });

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        // TODO [WARNING]: throwCount spin-loop read is non-volatile (same issue as above test method).
        // Fix: use Volatile.Read(ref throwCount) in the loop condition.
        // See Issue #2576 review findings (TestQualityReviewer [WARNING]).
        while (throwCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        stopCts.Cancel();
        await executeTask;

        // Assert: Error was emitted
        sink.Events
            .Should().Contain(e =>
                e.Level == LogEventLevel.Error &&
                e.Exception is InvalidOperationException,
            "InvalidOperationException must log at Error level in the outer poll-loop catch");
    }

    /// <summary>
    /// Regression test for Issue #2576: an HttpRequestException thrown inside a task passed to
    /// RunSafe must log at Warning (not Error). Uses reflection to invoke RunSafe directly to
    /// bypass ReconciliationLoop's internal exception absorption.
    /// </summary>
    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task RunSafe_WhenTaskThrowsHttpRequestException_LogsAtWarningNotError()
    {
        // Arrange
        var sink = new CapturingSink();
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var leaderElection = MakeLeaderElection(isLeader: false); // not the leader; we invoke RunSafe directly
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        // TODO [WARNING]: 'svc' below is dead code — RunSafe is static so it is invoked with null
        // instance via reflection. If RunSafe is ever made instance-level, the reflection call at
        // runSafe!.Invoke(null, ...) will silently test the wrong call path. Consider removing 'svc'
        // or guarding the invoke against null. See Issue #2576 review findings (DotNetSpecialist [WARNING]).
        var svc = MakeService(leaderElection);

        // Invoke RunSafe via reflection with a task that throws HttpRequestException
        var runSafe = typeof(ReconciliationService).GetMethod(
            "RunSafe", BindingFlags.NonPublic | BindingFlags.Static);
        runSafe.Should().NotBeNull("RunSafe must exist as a private static method");

        var throwingTask = Task.FromException(new HttpRequestException("simulated K8s API blip"));
        using var cts = new CancellationTokenSource();

        // TODO [WARNING]: The null-forgiving '!' below suppresses the null-check after the guard
        // assertion above. If RunSafe is renamed or made non-static, the reflection lookup returns
        // null and this line throws NullReferenceException instead of a meaningful assertion failure.
        // Consider using a null-check + Assert.Fail before proceeding.
        // See Issue #2576 review findings (TestQualityReviewer [WARNING]).
        await (Task)runSafe!.Invoke(null, [throwingTask, "ReconcileOnce", cts.Token])!;

        // Assert: Warning from RunSafe
        sink.Events
            .Should().Contain(e =>
                e.Level == LogEventLevel.Warning &&
                e.Exception is HttpRequestException &&
                e.RenderMessage().Contains("transient"),
            "HttpRequestException in RunSafe must log at Warning with 'transient' in the message");

        sink.Events
            .Should().NotContain(e =>
                e.Level == LogEventLevel.Error &&
                e.Exception is HttpRequestException,
            "HttpRequestException in RunSafe must NOT log at Error");
    }

    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task RunSafe_WhenTaskThrowsNonTransientException_LogsAtError()
    {
        // Arrange
        var sink = new CapturingSink();
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var leaderElection = MakeLeaderElection(isLeader: false);
        var svc = MakeService(leaderElection);

        var runSafe = typeof(ReconciliationService).GetMethod(
            "RunSafe", BindingFlags.NonPublic | BindingFlags.Static);
        runSafe.Should().NotBeNull("RunSafe must exist as a private static method");

        var throwingTask = Task.FromException(new InvalidOperationException("genuine bug in reconciliation"));
        using var cts = new CancellationTokenSource();

        await (Task)runSafe!.Invoke(null, [throwingTask, "ReconcileOnce", cts.Token])!;

        // Assert: Error from RunSafe
        sink.Events
            .Should().Contain(e =>
                e.Level == LogEventLevel.Error &&
                e.Exception is InvalidOperationException,
            "InvalidOperationException in RunSafe must log at Error");

        sink.Events
            .Should().NotContain(e =>
                e.Level == LogEventLevel.Warning &&
                e.Exception is InvalidOperationException,
            "InvalidOperationException in RunSafe must NOT log at Warning");
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

    /// <summary>
    /// Subclass with a fast (1-second) poll interval for log-level assertion tests,
    /// so recovery cycles happen within the test's deadline.
    /// </summary>
    private sealed class FastPollingReconciliationService : ReconciliationService
    {
        protected override int PollIntervalSeconds => 1;

        public FastPollingReconciliationService(ILeaderElectionService leaderElection, ReconciliationLoop loop)
            : base(leaderElection, loop)
        {
        }
    }

    /// <summary>
    /// Subclass that overrides OnPollCycleAsync to run a delegate before the real cycle, allowing
    /// tests to inject exceptions that bypass ReconciliationLoop's internal exception absorption.
    /// </summary>
    private sealed class ThrowingOnPollCycleReconciliationService : ReconciliationService
    {
        private readonly Action _onCycle;
        protected override int PollIntervalSeconds => 1;

        public ThrowingOnPollCycleReconciliationService(
            ILeaderElectionService leaderElection,
            ReconciliationLoop loop,
            Action onCycle)
            : base(leaderElection, loop)
        {
            _onCycle = onCycle;
        }

        protected override Task OnPollCycleAsync(CancellationToken ct)
        {
            _onCycle();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal Serilog sink that captures log events for assertion.
    /// Thread-safe: uses <see cref="ConcurrentQueue{T}"/> so that concurrent Serilog background
    /// thread emissions do not race with the test spin-loop's reads.
    /// </summary>
    // TODO [WARNING]: The async loop tests mutate the process-global Serilog.Log.Logger; if two
    // test classes run in parallel, events from one class can land in the other's CapturingSink.
    // Neither ReconciliationServiceTests nor LeaderElectedPollingServiceTests uses [Collection]
    // isolation. Add [Collection("SerialSerilogTests")] to both classes to enforce serial execution,
    // or switch to scoped Serilog Logger instances rather than the global static.
    // See Issue #2576 review findings (DotNetSpecialist [WARNING], TestQualityReviewer [WARNING]).
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public IEnumerable<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
