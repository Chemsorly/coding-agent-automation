using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationRetryService"/>.
/// Tests are focused on the <see cref="ConsolidationRetryService.RunRetrySweepAsync"/> method
/// (the inner sweep logic), which is called by the <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// loop on every periodic tick.
/// </summary>
public sealed class ConsolidationRetryServiceTests
{
    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IConsolidationDispatcher> _dispatcher = new();

    private ConsolidationRetryService CreateSut() => new(
        _consolidationService.Object,
        _dispatcher.Object,
        Log.Logger);

    // ── No queued runs ────────────────────────────────────────────────────

    /// <summary>
    /// When there are no queued consolidation runs, the dispatcher must not be called.
    /// </summary>
    [Fact]
    public async Task RunRetrySweepAsync_NoQueuedRuns_DoesNotCallDispatcher()
    {
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());

        var sut = CreateSut();
        await sut.RunRetrySweepAsync(CancellationToken.None);

        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "No runs queued — dispatcher must not be invoked");
    }

    /// <summary>
    /// When there are queued consolidation runs, the dispatcher must be called once per run.
    /// </summary>
    [Fact]
    public async Task RunRetrySweepAsync_WithQueuedRuns_DispatchesEachRun()
    {
        var run1 = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        var run2 = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run1, run2 });

        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.RunRetrySweepAsync(CancellationToken.None);

        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Dispatcher must be called once for each queued run");
    }

    /// <summary>
    /// Each queued run is dispatched with that specific run object.
    /// </summary>
    [Fact]
    public async Task RunRetrySweepAsync_WithQueuedRuns_PassesCorrectRunToDispatcher()
    {
        var runId = Guid.NewGuid().ToString();
        var queuedRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.HarnessSuggestions,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { queuedRun });

        ConsolidationRun? dispatched = null;
        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRun, CancellationToken>((r, _) => dispatched = r)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.RunRetrySweepAsync(CancellationToken.None);

        Assert.NotNull(dispatched);
        Assert.Equal(runId, dispatched!.RunId);
        Assert.Equal(ConsolidationRunType.HarnessSuggestions, dispatched.Type);
    }

    // ── Error resilience ──────────────────────────────────────────────────

    /// <summary>
    /// When <see cref="IConsolidationService.RehydrateQueuedRunsAsync"/> throws, the sweep
    /// must not propagate the exception — it logs a warning and returns.
    /// </summary>
    [Fact]
    public async Task RunRetrySweepAsync_RehydrateThrows_DoesNotThrow()
    {
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var sut = CreateSut();
        // Must not throw — exception is swallowed and logged
        await sut.RunRetrySweepAsync(CancellationToken.None);

        _dispatcher.Verify(
            d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Dispatcher must not be called when rehydration throws");
    }

    /// <summary>
    /// DispatchRunAsync is responsible for swallowing its own errors. This test confirms
    /// that if the dispatcher throws (misconfigured mock), the sweep itself still handles it.
    /// In practice, ConsolidationDispatcher never throws — it has its own outer catch.
    /// </summary>
    [Fact]
    public async Task RunRetrySweepAsync_DispatcherThrows_DoesNotPropagateAndContinues()
    {
        var run1 = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        var run2 = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run1, run2 });

        // First call throws, second should still be attempted
        var callCount = 0;
        _dispatcher
            .Setup(d => d.DispatchRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromException(new InvalidOperationException("Dispatch error on first run"));
                return Task.CompletedTask;
            });

        var sut = CreateSut();
        // Must not throw
        await sut.RunRetrySweepAsync(CancellationToken.None);
    }

    // ── Default interval ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies the default interval is 5 minutes, as documented.
    /// </summary>
    [Fact]
    public void DefaultInterval_IsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ConsolidationRetryService.DefaultInterval);
    }
}
