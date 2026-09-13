using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationRetryBackgroundService.RetryQueuedRunsAsync"/>.
/// Exercises the bounded retry sweep introduced as a CRITICAL fix for issue #2536:
/// transient dispatch failures (409/503) must be retried on a schedule, not only on restart.
/// </summary>
public sealed class ConsolidationRetryBackgroundServiceTests
{
    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IConsolidationDispatcher> _dispatcher = new();

    private ConsolidationRetryBackgroundService CreateSut() =>
        new(
            _consolidationService.Object,
            _dispatcher.Object,
            TimeProvider.System,
            Log.Logger);

    private static ConsolidationRun MakeQueuedRun(string? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        Status = ConsolidationRunStatus.Queued,
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    // ── No queued runs ────────────────────────────────────────────────────

    [Fact]
    public async Task RetryQueuedRunsAsync_NoQueuedRuns_DoesNotCallDispatcher()
    {
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());

        var sut = CreateSut();
        await sut.RetryQueuedRunsAsync(CancellationToken.None);

        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no queued runs → dispatcher must not be called");
    }

    // ── Queued runs dispatched ────────────────────────────────────────────

    [Fact]
    public async Task RetryQueuedRunsAsync_WithQueuedRuns_DispatchesEach()
    {
        var runs = new[] { MakeQueuedRun(), MakeQueuedRun() };
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(runs);

        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.RetryQueuedRunsAsync(CancellationToken.None);

        // Each queued run must be dispatched exactly once per sweep.
        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "each queued run must be dispatched once per retry sweep");
    }

    [Fact]
    public async Task RetryQueuedRunsAsync_DispatchesRunWithCorrectRunId()
    {
        var expectedRunId = Guid.NewGuid().ToString();
        var run = MakeQueuedRun(expectedRunId);
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run });

        ConsolidationRun? captured = null;
        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRun, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.RetryQueuedRunsAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(expectedRunId, captured!.RunId);
    }

    // ── Resilience: dispatcher throws unexpectedly ────────────────────────

    /// <summary>
    /// If DispatchRunAsync throws unexpectedly (infrastructure error), the sweep must
    /// continue to retry the remaining runs and must NOT throw.
    /// DispatchRunAsync is documented to never throw for dispatch errors; this guard covers
    /// unexpected infrastructure failures.
    /// </summary>
    [Fact]
    public async Task RetryQueuedRunsAsync_DispatcherThrows_SwallowsAndContinues()
    {
        var run1 = MakeQueuedRun("run-1");
        var run2 = MakeQueuedRun("run-2");
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run1, run2 });

        // First run throws; second should still be dispatched.
        _dispatcher
            .Setup(d => d.DispatchRunAsync(
                It.Is<ConsolidationRun>(r => r.RunId == "run-1"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated infra failure"));

        _dispatcher
            .Setup(d => d.DispatchRunAsync(
                It.Is<ConsolidationRun>(r => r.RunId == "run-2"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        // Must not throw
        await sut.RetryQueuedRunsAsync(CancellationToken.None);

        // run-2 must still be attempted even though run-1 threw
        _dispatcher.Verify(
            d => d.DispatchRunAsync(
                It.Is<ConsolidationRun>(r => r.RunId == "run-2"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a per-run failure must not abort the remaining runs in the sweep");
    }

    // ── Resilience: RehydrateQueuedRunsAsync throws ───────────────────────

    [Fact]
    public async Task RetryQueuedRunsAsync_RehydrateThrows_SwallowsAndDoesNotDispatch()
    {
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var sut = CreateSut();

        // Must not throw
        await sut.RetryQueuedRunsAsync(CancellationToken.None);

        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "if loading runs fails, the dispatcher must not be called");
    }

    // ── Cancellation ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetryQueuedRunsAsync_CancellationRequested_StopsBeforeDispatching()
    {
        var run = MakeQueuedRun();
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run });

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var sut = CreateSut();
        await sut.RetryQueuedRunsAsync(cts.Token);

        // Cancelled token — RehydrateQueuedRunsAsync was called but dispatch loop should exit early.
        // Dispatcher should not be called.
        // TODO [WARNING]: This test conflates two distinct cancellation paths: (a) the token is
        // forwarded to RehydrateQueuedRunsAsync which could throw OperationCanceledException before
        // the foreach, and (b) the foreach loop guard `if (ct.IsCancellationRequested) break`.
        // The mock ignores the token and returns runs, so only path (b) is actually exercised.
        // The assertion passes even if the foreach loop guard is deleted (because the mock never
        // throws on cancellation). Consider splitting into two tests: one where the store forwards
        // the token and throws OCE, one where the store returns runs but the loop guard fires.
        // (review-findings.md TestQualityReviewer warning, ConsolidationRetryBackgroundServiceTests.cs:182)
        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a pre-cancelled token must stop the sweep before dispatching any run");
    }
}
