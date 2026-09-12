using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ConsolidationTransientRetryService"/>, the bounded background retry sweep
/// that periodically re-dispatches Queued consolidation runs so they are not stuck forever waiting
/// for the next orchestrator restart.
/// </summary>
public sealed class ConsolidationTransientRetryServiceTests
{
    private static readonly TimeSpan TestInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallWait = TimeSpan.FromSeconds(10);

    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IConsolidationDispatcher> _dispatcher = new();

    private static readonly DateTimeOffset TestOrigin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private ConsolidationTransientRetryService CreateSut(FakeTimeProvider clock) =>
        new(
            _consolidationService.Object,
            _dispatcher.Object,
            clock,
            CreateLogger())
        {
            RetryInterval = TestInterval,
            MaxRetryAttempts = 3
        };

    private static ILogger CreateLogger()
    {
        var mock = new Mock<ILogger>();
        // Serilog ILogger.ForContext<T>() is called in the constructor; must return a usable logger
        mock.Setup(l => l.ForContext<ConsolidationTransientRetryService>()).Returns(mock.Object);
        mock.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>())).Returns(mock.Object);
        return mock.Object;
    }

    private ConsolidationRun MakeQueuedRun(string? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        Status = ConsolidationRunStatus.Queued,
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    // ── Happy path ────────────────────────────────────────────────────────────

    /// <summary>
    /// When there are Queued runs, the service must call RehydrateQueuedRunsAsync and dispatch
    /// each run via IConsolidationDispatcher on the first tick.
    /// </summary>
    // TODO: _dispatcher.Verify uses Times.AtLeastOnce for both run1 and run2. Because the service
    // timer is not stopped before the verification (StopAsync is only called in finally), the service
    // may have ticked multiple times, making the assertion pass vacuously on attempt 1. A bug where
    // only one run is dispatched per sweep (early break/continue) would not be caught. Consider
    // stopping the service before verification, or pinning Times.Exactly(1) after exactly one tick.
    // See review-findings.md [WARNING] ConsolidationTransientRetryServiceTests.cs:63.
    [Fact]
    public async Task OnEachTick_DispatchesAllQueuedRuns()
    {
        var clock = new FakeTimeProvider(TestOrigin);
        var rehydrateSignal = new SemaphoreSlim(0);
        var run1 = MakeQueuedRun();
        var run2 = MakeQueuedRun();

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                rehydrateSignal.Release();
                return Task.FromResult<IReadOnlyList<ConsolidationRun>>(new[] { run1, run2 });
            });

        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(clock);
        await sut.StartAsync(CancellationToken.None);
        try
        {
            (await rehydrateSignal.WaitAsync(CallWait)).Should().BeTrue("service must tick immediately on start");

            await WaitUntilAsync(() =>
                _dispatcher.Invocations.Count(i => i.Method.Name == nameof(IConsolidationDispatcher.DispatchRunAsync)) >= 2);

            _dispatcher.Verify(d => d.DispatchRunAsync(run1, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
            _dispatcher.Verify(d => d.DispatchRunAsync(run2, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// When there are no Queued runs, the service must not call DispatchRunAsync at all.
    /// </summary>
    [Fact]
    public async Task WhenNoQueuedRuns_DoesNotCallDispatcher()
    {
        var clock = new FakeTimeProvider(TestOrigin);
        var rehydrateSignal = new SemaphoreSlim(0);

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                rehydrateSignal.Release();
                return Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>());
            });

        var sut = CreateSut(clock);
        await sut.StartAsync(CancellationToken.None);
        try
        {
            (await rehydrateSignal.WaitAsync(CallWait)).Should().BeTrue();
            await Task.Delay(50); // let any pending dispatch fire if it were going to
            _dispatcher.Verify(
                d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    // ── MaxRetryAttempts bound ────────────────────────────────────────────────

    /// <summary>
    /// After a run has been dispatched MaxRetryAttempts times without leaving Queued status,
    /// the next tick must cascade it to Failed via UpdateRunAsync instead of dispatching again.
    /// This is the "bounded" guarantee — runs cannot spin in Queued indefinitely.
    /// </summary>
    [Fact]
    public async Task WhenRunExceedsMaxRetryAttempts_CascadesToFailed_AndIsNotDispatched()
    {
        var runId = Guid.NewGuid().ToString();
        var run = MakeQueuedRun(runId);

        var tickCount = 0;
        var cascadeSignal = new SemaphoreSlim(0);
        const int maxAttempts = 3;

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref tickCount);
                return Task.FromResult<IReadOnlyList<ConsolidationRun>>(new[] { run });
            });

        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(),
                ConsolidationRunStatus.Failed,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<long>()))
            .Returns(() =>
            {
                cascadeSignal.Release();
                return Task.CompletedTask;
            });

        var clock = new FakeTimeProvider(TestOrigin);
        var sut = CreateSut(clock);
        // MaxRetryAttempts = 3 via CreateSut
        await sut.StartAsync(CancellationToken.None);
        try
        {
            // Tick 1 (attempt 1) — dispatch
            await WaitUntilAsync(() => tickCount >= 1);
            // Tick 2 (attempt 2) — dispatch
            clock.Advance(TestInterval);
            await WaitUntilAsync(() => tickCount >= 2);
            // Tick 3 (attempt 3) — dispatch
            clock.Advance(TestInterval);
            await WaitUntilAsync(() => tickCount >= 3);
            // Tick 4 (maxAttempts exceeded) — must cascade to Failed, not dispatch
            clock.Advance(TestInterval);
            (await cascadeSignal.WaitAsync(CallWait)).Should().BeTrue(
                $"after {maxAttempts} attempts the run must be cascaded to Failed, not dispatched again");

            _consolidationService.Verify(
                s => s.UpdateRunAsync(
                    It.Is<RunId>(r => r == new RunId(runId)),
                    ConsolidationRunStatus.Failed,
                    It.Is<string?>(msg => msg != null && msg.Contains("retry")),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<long>()),
                Times.Once,
                "UpdateRunAsync(Failed) must be called exactly once when retry limit is exceeded");

            // After cascade the run is no longer tracked — dispatch count must equal MaxRetryAttempts
            _dispatcher.Verify(
                d => d.DispatchRunAsync(run, It.IsAny<CancellationToken>()),
                Times.Exactly(maxAttempts),
                "DispatchRunAsync must be called exactly MaxRetryAttempts times before cascade");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A run that has not yet reached MaxRetryAttempts must continue to be dispatched normally —
    /// verifies the count does not prematurely trigger the cascade.
    /// </summary>
    // TODO: This test only observes attempt 1 (the initial tick) before StopAsync. It does not
    // advance the clock or observe attempts 2 and 3, so it cannot detect a fence-post bug where
    // the cascade fires at previousAttempts >= MaxRetryAttempts - 1 instead of >= MaxRetryAttempts.
    // Fix: advance the clock (MaxRetryAttempts - 1) times via clock.Advance(TestInterval), wait for
    // each tick to complete, then verify UpdateRunAsync(Failed) is still Times.Never after all
    // sub-limit attempts, and only fires on the (MaxRetryAttempts + 1)-th tick.
    // See review-findings.md [WARNING] ConsolidationTransientRetryServiceTests.cs:215.
    [Fact]
    public async Task WhenRunBelowMaxRetryAttempts_StillDispatchesNormally_NoCascade()
    {
        var runId = Guid.NewGuid().ToString();
        var run = MakeQueuedRun(runId);
        var dispatchSignal = new SemaphoreSlim(0);

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run });

        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                dispatchSignal.Release();
                return Task.CompletedTask;
            });

        var clock = new FakeTimeProvider(TestOrigin);
        var sut = CreateSut(clock); // MaxRetryAttempts = 3
        await sut.StartAsync(CancellationToken.None);
        try
        {
            // First attempt — must dispatch
            (await dispatchSignal.WaitAsync(CallWait)).Should().BeTrue("first attempt must dispatch");

            // No cascade must have fired yet
            _consolidationService.Verify(
                s => s.UpdateRunAsync(
                    It.IsAny<RunId>(), ConsolidationRunStatus.Failed,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
                Times.Never,
                "UpdateRunAsync(Failed) must not fire before MaxRetryAttempts is exceeded");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    // ── Resilience ────────────────────────────────────────────────────────────

    /// <summary>
    /// A transient exception from RehydrateQueuedRunsAsync must not kill the background loop.
    /// The loop must survive and retry on the next tick.
    /// </summary>
    [Fact]
    public async Task WhenRehydrateThrows_LoopSurvivesAndRecoversOnNextTick()
    {
        var clock = new FakeTimeProvider(TestOrigin);
        var attempt = 0;
        var recoveredSignal = new SemaphoreSlim(0);

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var n = Interlocked.Increment(ref attempt);
                if (n == 1)
                    throw new InvalidOperationException("run store unavailable");

                recoveredSignal.Release();
                return Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>());
            });

        var sut = CreateSut(clock);
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => attempt >= 1); // first tick throws
            clock.Advance(TestInterval);
            (await recoveredSignal.WaitAsync(CallWait)).Should().BeTrue(
                "loop must recover after a failed tick — PeriodicTimer loop must survive exceptions");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// StopAsync must complete cleanly — OperationCanceledException from PeriodicTimer is internal.
    /// </summary>
    [Fact]
    public async Task StopAsync_CompletesCleanly_NoFaultedTask()
    {
        var clock = new FakeTimeProvider(TestOrigin);
        var ticked = new SemaphoreSlim(0);

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                ticked.Release();
                return Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>());
            });

        var sut = CreateSut(clock);
        await sut.StartAsync(CancellationToken.None);
        (await ticked.WaitAsync(CallWait)).Should().BeTrue();

        var stop = async () => await sut.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync("StopAsync must absorb OperationCanceledException from PeriodicTimer");

        sut.ExecuteTask.Should().NotBeNull();
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ── Retry counter is per run-ID ───────────────────────────────────────────

    /// <summary>
    /// Two distinct runs must have independent retry counters. Exhausting one run's budget
    /// must not cascade the other.
    /// </summary>
    // TODO: The cascade detection for runB uses `await WaitUntilAsync(() => cascadeCallCount >= 1)`,
    // which polls with a real-wall-clock deadline. If UpdateRunAsync throws or the clock.Advance
    // races with the sweep completing, WaitUntilAsync times out silently (returns void without
    // asserting), and the subsequent Verify(Times.Once) may execute against zero calls and report
    // a false failure. Use the SemaphoreSlim pattern from WhenRunExceedsMaxRetryAttempts_CascadesToFailed
    // (cascadeSignal.WaitAsync with a timeout assertion) for reliable cascade detection here too.
    // See review-findings.md [WARNING] ConsolidationTransientRetryServiceTests.cs:341.
    [Fact]
    public async Task PerRunRetryCounter_IsIndependent_OtherRunNotAffected()
    {
        var runA = MakeQueuedRun();
        var runB = MakeQueuedRun();

        var tickCount = 0;
        var cascadeCallCount = 0;

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref tickCount);
                // runA disappears after tick 1 (e.g. it succeeded), runB stays Queued indefinitely
                return Task.FromResult<IReadOnlyList<ConsolidationRun>>(
                    tickCount == 1 ? new[] { runA, runB } : new[] { runB });
            });

        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), ConsolidationRunStatus.Failed,
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref cascadeCallCount);
                return Task.CompletedTask;
            });

        var clock = new FakeTimeProvider(TestOrigin);
        var sut = CreateSut(clock); // MaxRetryAttempts = 3
        await sut.StartAsync(CancellationToken.None);
        try
        {
            // Ticks 1-3: runB dispatched each time (attempts 1, 2, 3)
            await WaitUntilAsync(() => tickCount >= 1);
            clock.Advance(TestInterval);
            await WaitUntilAsync(() => tickCount >= 2);
            clock.Advance(TestInterval);
            await WaitUntilAsync(() => tickCount >= 3);
            // Tick 4: runB has exhausted MaxRetryAttempts → cascade
            clock.Advance(TestInterval);
            await WaitUntilAsync(() => cascadeCallCount >= 1);

            // Only runB must have been cascaded, not runA (which left the queue naturally)
            _consolidationService.Verify(
                s => s.UpdateRunAsync(
                    It.Is<RunId>(r => r == new RunId(runB.RunId)),
                    ConsolidationRunStatus.Failed,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
                Times.Once,
                "only runB must be cascaded — it is the one that exhausted its retry budget");

            _consolidationService.Verify(
                s => s.UpdateRunAsync(
                    It.Is<RunId>(r => r == new RunId(runA.RunId)),
                    ConsolidationRunStatus.Failed,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
                Times.Never,
                "runA must NOT be cascaded — it left the Queued list naturally (no budget exhaustion)");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + CallWait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
