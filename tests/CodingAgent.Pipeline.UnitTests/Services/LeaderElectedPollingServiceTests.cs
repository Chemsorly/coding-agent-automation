using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Reflection;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

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

    // ── Transient exception log-level classification ─────────────────────────

    /// <summary>
    /// Regression test for Issue #2576: a transient HttpRequestException from OnPollCycleAsync
    /// must be logged at Warning level (not Error) and must not terminate the loop.
    /// Uses a real Serilog sink (not Mock ILogger) to avoid the false-positive issue noted in
    /// PipelineLoopServiceTransientResilienceTests — mock Verify() on structured templates
    /// does not match generic overloads.
    /// </summary>
    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task OnPollCycleAsync_WhenThrowsHttpRequestException_LogsAtWarningNotError()
    {
        // Arrange: capture log events via a real Serilog sink.
        // Save and restore the global logger so concurrent tests running in parallel cannot
        // see events from this test's CapturingSink (LeaderElectedPollingService.Log is a
        // property that re-evaluates Serilog.Log.Logger on every call).
        var previousLogger = Serilog.Log.Logger;
        var sink = new CapturingSink();
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var leaderElection = CreateLeaderElection(isLeader: true, new CancellationTokenSource());
        // First call throws HttpRequestException (transient); subsequent calls succeed
        var service = new TestThrowingServiceWithException(
            leaderElection,
            pollIntervalSeconds: 1,
            exceptionOnFirstCall: new HttpRequestException("connection refused"));
        var hostCts = new CancellationTokenSource();

        try
        {
            var executeTask = InvokeExecuteAsync(service, hostCts.Token);

            // Wait for the exception to be thrown and handled (poll count > 1 means we recovered)
            var deadline = DateTime.UtcNow.AddSeconds(10);
            // TODO [WARNING]: PollCycleCount is a plain int field; Interlocked.Increment writes it but
            // this spin-loop read is non-volatile. On Release builds with register caching, the test
            // thread may never observe PollCycleCount >= 2 and spin to the deadline. Fix: use
            // Volatile.Read(ref service.PollCycleCount) in the loop condition (requires making the
            // field accessible, or adding a volatile-read helper property).
            // See Issue #2576 review findings (TestQualityReviewer [WARNING]).
            while (service.PollCycleCount < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            hostCts.Cancel();
            await WaitForTaskCompletion(executeTask);

            // Assert: loop survived (count > 1)
            service.PollCycleCount.Should().BeGreaterThanOrEqualTo(2,
                "loop must continue after a transient HttpRequestException");

            // Assert: Warning was emitted for the transient exception
            sink.Events
                .Should().Contain(e =>
                    e.Level == LogEventLevel.Warning &&
                    e.RenderMessage().Contains("transient"),
                "HttpRequestException must log at Warning level with 'transient' in the message");

            // Assert: no Error was emitted for the transient exception
            sink.Events
                .Should().NotContain(e =>
                    e.Level == LogEventLevel.Error &&
                    e.Exception is HttpRequestException,
                "HttpRequestException must NOT be logged at Error level");
        }
        finally
        {
            hostCts.Cancel();
            Serilog.Log.Logger = previousLogger;
        }
    }

    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task OnPollCycleAsync_WhenThrowsNonTransientException_LogsAtError()
    {
        // Arrange: save and restore the global logger so concurrent tests cannot inject
        // Warning+InvalidOperationException events into this test's CapturingSink.
        // ModelFetchJobService (same assembly) logs Warning(InvalidOperationException) for
        // cleanup/PVC-selection failures; without the restore those events cross test boundaries
        // and trip the NotContain assertion below.
        var previousLogger = Serilog.Log.Logger;
        var sink = new CapturingSink();
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var leaderElection = CreateLeaderElection(isLeader: true, new CancellationTokenSource());
        var service = new TestThrowingServiceWithException(
            leaderElection,
            pollIntervalSeconds: 1,
            exceptionOnFirstCall: new InvalidOperationException("genuine bug"));
        var hostCts = new CancellationTokenSource();

        try
        {
            var executeTask = InvokeExecuteAsync(service, hostCts.Token);

            // Wait for the exception to be thrown and handled
            var deadline = DateTime.UtcNow.AddSeconds(10);
            // TODO [WARNING]: PollCycleCount spin-loop read is non-volatile (same issue as above test method).
            // Fix: use Volatile.Read in the loop condition. See Issue #2576 review findings (TestQualityReviewer [WARNING]).
            while (service.PollCycleCount < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            hostCts.Cancel();
            await WaitForTaskCompletion(executeTask);

            // Assert: Error was emitted for the non-transient exception
            sink.Events
                .Should().Contain(e =>
                    e.Level == LogEventLevel.Error &&
                    e.Exception is InvalidOperationException,
                "InvalidOperationException must log at Error level");

            // Assert: no Warning at all for this exception (it must go to Error, not Warning)
            sink.Events
                .Should().NotContain(e =>
                    e.Level == LogEventLevel.Warning &&
                    e.Exception is InvalidOperationException,
                "non-transient exception must NOT be logged at Warning level");
        }
        finally
        {
            hostCts.Cancel();
            Serilog.Log.Logger = previousLogger;
        }
    }

    [Fact]
    [Trait("Feature", "ConnectionResiliency")]
    public async Task OnPollCycleAsync_WhenThrowsTransientException_LoopContinues()
    {
        // Arrange: throw on first 3 calls to confirm recovery in all cases
        var leaderElection = CreateLeaderElection(isLeader: true, new CancellationTokenSource());
        var service = new TestThrowingServiceWithException(
            leaderElection,
            pollIntervalSeconds: 1,
            exceptionOnFirstCall: new TimeoutException("simulated transient timeout"),
            throwOnFirstNCalls: 3);
        var hostCts = new CancellationTokenSource();

        var executeTask = InvokeExecuteAsync(service, hostCts.Token);

        // After 3 throws the 4th call should succeed — wait for at least 4 cycles
        var deadline = DateTime.UtcNow.AddSeconds(15);
        // TODO [WARNING]: PollCycleCount spin-loop read is non-volatile (same issue as above test methods).
        // Fix: use Volatile.Read in the loop condition. See Issue #2576 review findings (TestQualityReviewer [WARNING]).
        while (service.PollCycleCount < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        hostCts.Cancel();
        await WaitForTaskCompletion(executeTask);

        service.PollCycleCount.Should().BeGreaterThanOrEqualTo(4,
            "loop must keep running after repeated transient exceptions");
    }

    // ── IsTransientPollingException contract ────────────────────────────────

    // TODO [WARNING]: BrokenCircuitException is classified as transient in IsTransientPollingException
    // (production code) but is absent from the [InlineData] set below. A future change removing it
    // from the predicate would not be caught. Add [InlineData(typeof(BrokenCircuitException))] here
    // to close the gap. Note: BrokenCircuitException has no public no-arg constructor, so it needs
    // instantiation via Activator or a direct new() call with a message argument.
    // See Issue #2576 review findings (TestQualityReviewer [WARNING]).
    [Theory]
    [Trait("Feature", "ConnectionResiliency")]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(System.IO.IOException))]
    public void IsTransientPollingException_KnownTransientTypes_ReturnsTrue(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        TestPollingServiceWithVisibleHelper.CallIsTransient(ex).Should().BeTrue(
            $"{exceptionType.Name} must be classified as transient");
    }

    [Theory]
    [Trait("Feature", "ConnectionResiliency")]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(NotSupportedException))]
    public void IsTransientPollingException_NonTransientTypes_ReturnsFalse(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        TestPollingServiceWithVisibleHelper.CallIsTransient(ex).Should().BeFalse(
            $"{exceptionType.Name} must NOT be classified as transient");
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
        var executeMethod = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var executeTask = (Task)executeMethod!.Invoke(service, [hostCts.Token])!;

        // Wait (event-driven) until RunLeadershipTermAsync has been entered — the service is now
        // inside await Task.Delay(Timeout.Infinite, ct) and will respond to cancellation.
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

    /// <summary>
    /// Test double for transient/non-transient exception classification tests.
    /// Throws a configurable exception on the first N poll cycles.
    /// </summary>
    private sealed class TestThrowingServiceWithException : LeaderElectedPollingService
    {
        private readonly Exception _exceptionOnFirstCall;
        private readonly int _throwOnFirstNCalls;
        public int PollCycleCount;

        protected override string ServiceName => "TestThrowingServiceWithException";
        protected override int PollIntervalSeconds { get; }

        public TestThrowingServiceWithException(
            ILeaderElectionService leaderElection,
            int pollIntervalSeconds,
            Exception exceptionOnFirstCall,
            int throwOnFirstNCalls = 1)
            : base(leaderElection)
        {
            PollIntervalSeconds = pollIntervalSeconds;
            _exceptionOnFirstCall = exceptionOnFirstCall;
            _throwOnFirstNCalls = throwOnFirstNCalls;
        }

        protected override Task OnPollCycleAsync(CancellationToken ct)
        {
            var count = Interlocked.Increment(ref PollCycleCount);
            if (count <= _throwOnFirstNCalls)
                throw _exceptionOnFirstCall;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Exposes <see cref="LeaderElectedPollingService.IsTransientPollingException"/> for
    /// contract tests via a concrete subclass (protected method accessible from subclass).
    /// </summary>
    private sealed class TestPollingServiceWithVisibleHelper : LeaderElectedPollingService
    {
        protected override string ServiceName => "TestPollingServiceWithVisibleHelper";
        protected override int PollIntervalSeconds => 1;

        public TestPollingServiceWithVisibleHelper(ILeaderElectionService leaderElection)
            : base(leaderElection) { }

        protected override Task OnPollCycleAsync(CancellationToken ct) => Task.CompletedTask;

        public static bool CallIsTransient(Exception ex) => IsTransientPollingException(ex);
    }

    /// <summary>
    /// Minimal Serilog sink that captures log events for assertion.
    /// Thread-safe: uses <see cref="ConcurrentQueue{T}"/> so that concurrent Serilog background
    /// thread emissions do not race with the test spin-loop's reads.
    /// </summary>
    // TODO [WARNING]: The async loop tests mutate the process-global Serilog.Log.Logger; if two
    // test classes run in parallel, events from one class can land in the other's CapturingSink.
    // Neither LeaderElectedPollingServiceTests nor ReconciliationServiceTests uses [Collection]
    // isolation. Add [Collection("SerialSerilogTests")] to both classes to enforce serial execution,
    // or switch to scoped Serilog Logger instances rather than the global static.
    // See Issue #2576 review findings (DotNetSpecialist [WARNING]).
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public IEnumerable<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
