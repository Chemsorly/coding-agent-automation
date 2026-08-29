using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Serilog;

namespace CodingAgentWebUI.JobController.Reconciliation;

/// <summary>
/// Leader-elected BackgroundService that reconciles K8s Job state with WorkItem state.
/// Runs under the same dispatch leader lease. Runs three periodic loops concurrently:
/// - Reconciliation (completed/failed jobs → post status)
/// - Timeout enforcement (session max duration)
/// - Dispatched timeout sweep (short-circuit orphaned claims)
/// - Orphan cleanup (stale Jobs with no active WorkItem)
///
/// Implements <see cref="IReconciliationTrigger"/> so that <see cref="CodingAgentWebUI.JobController.Dispatch.DispatchLoop"/>
/// and <see cref="CodingAgentWebUI.JobController.Dispatch.ConsolidationDispatchLoop"/>
/// can request an early reconciliation cycle when the PVC pool is exhausted, rather than
/// waiting up to 30 seconds for the regular poll interval.
/// </summary>
// ReconciliationService was changed from sealed to public class to allow
// TestableReconciliationService to subclass it in tests. This is a test-driven design leak.
// Consider injecting PollIntervalSeconds via constructor/options and restoring sealed, or
// using [InternalsVisibleTo] with an internal class instead.
public class ReconciliationService : LeaderElectedPollingService, IReconciliationTrigger
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ReconciliationService>();

    private readonly ReconciliationLoop _loop;

    // maxCount: 1 means at most one pending wake signal is ever accumulated.
    // RequestImmediateCycle() is idempotent: N calls before the service wakes
    // produce exactly one extra cycle.
    private readonly SemaphoreSlim _wakeSignal = new(initialCount: 0, maxCount: 1);

    protected override string ServiceName => "ReconciliationService";
    protected override int PollIntervalSeconds => 30; // fixed; reconciliation doesn't need to match dispatch cadence

    public ReconciliationService(
        ILeaderElectionService leaderElection,
        ReconciliationLoop loop)
        : base(leaderElection) // no rate limiter — reconciliation doesn't need throttling
    {
        ArgumentNullException.ThrowIfNull(leaderElection);
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        // base.Dispose() cancels the internal CancellationTokenSource (via BackgroundService),
        // which causes RunLeadershipTermAsync to exit _wakeSignal.WaitAsync(ct) cleanly.
        // Disposing _wakeSignal first (old order) risked ObjectDisposedException on the
        // background thread if Dispose() was called without a preceding StopAsync().
        base.Dispose();
        _wakeSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc cref="IReconciliationTrigger.RequestImmediateCycle"/>
    public void RequestImmediateCycle()
    {
        // No guard against post-Dispose calls. _wakeSignal.Release() on a
        // disposed SemaphoreSlim throws ObjectDisposedException. DispatchLoop and
        // ConsolidationDispatchLoop hold a reference to IReconciliationTrigger and may call
        // this during shutdown, racing with Dispose(). Consider catching ObjectDisposedException
        // or checking a disposed flag before calling Release().
        // Try to release the semaphore. SemaphoreFullException means it was already
        // signalled (CurrentCount == maxCount == 1) — the pending wake covers this request,
        // so swallow the exception. This is safe under concurrent callers: checking
        // CurrentCount before Release() has a TOCTOU race; try/catch is the safe approach.
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled — the next wake will handle this request. No-op.
        }
    }

    /// <summary>
    /// Overrides the base class poll loop to support early wake-up via <see cref="RequestImmediateCycle"/>.
    /// Replaces the plain <c>Task.Delay(PollIntervalSeconds, ct)</c> with
    /// <c>Task.WhenAny(Task.Delay(...), _wakeSignal.WaitAsync(ct))</c> so that
    /// a triggered reconciliation fires within milliseconds rather than up to 30 seconds.
    /// <para>
    /// <see cref="OnPollCycleAsync"/> never runs concurrently with itself: the loop awaits
    /// the delay/wake only after the previous cycle completes, so concurrent trigger calls
    /// at most advance the next scheduled cycle — they do not overlap cycles.
    /// </para>
    /// </summary>
    protected override async Task RunLeadershipTermAsync(CancellationToken ct)
    {
        // Drain any signals that accumulated while this instance was not the leader.
        // Without this, a signal posted before leadership is acquired causes an extra
        // early cycle immediately on start — harmless but unnecessary.
        while (await _wakeSignal.WaitAsync(0, CancellationToken.None))
        {
            // drained one pending signal, continue until empty
        }

        Log.Information("ReconciliationService: leader acquired, entering poll loop");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await OnPollCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReconciliationService: unhandled error in poll cycle");
            }

            try
            {
                // Wake on trigger signal OR after normal poll interval, whichever comes first.
                // The WhenAny itself does not block the current cycle from completing — it is
                // only reached after OnPollCycleAsync returns.
                //
                // A per-iteration CancellationTokenSource is used to cancel whichever task
                // loses the WhenAny race. Without this, the losing _wakeSignal.WaitAsync(ct)
                // remains as a pending waiter on the SemaphoreSlim. On the next iteration a
                // second WaitAsync is registered; when RequestImmediateCycle() calls Release(),
                // the SemaphoreSlim may satisfy the orphaned waiter from the previous iteration
                // instead of the current one — consuming the wake signal without waking the
                // current loop iteration. Over N poll cycles, N orphaned waiters accumulate and
                // the triggered-wake feature silently degrades.
                using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), iterationCts.Token),
                    _wakeSignal.WaitAsync(iterationCts.Token));
                // Cancel the losing task to dequeue it from the semaphore and dispose the timer.
                await iterationCts.CancelAsync();

                // Drain any extra signals that arrived while the cycle was running or
                // while we were in Task.Delay. With maxCount: 1 this is at most one drain.
                while (await _wakeSignal.WaitAsync(0, CancellationToken.None))
                {
                    // drained one pending signal, continue until empty
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    protected override async Task OnPollCycleAsync(CancellationToken ct)
    {
        Log.Debug("ReconciliationService: starting reconciliation cycle");

        // Run all reconciliation tasks concurrently within the same poll cycle
        await Task.WhenAll(
            RunSafe(_loop.ReconcileOnceAsync(ct), "ReconcileOnce", ct),
            RunSafe(_loop.EnforceTimeoutsAsync(ct), "EnforceTimeouts", ct),
            RunSafe(_loop.EnforceDispatchedTimeoutAsync(ct), "EnforceDispatchedTimeout", ct),
            RunSafe(_loop.CleanupOrphansAsync(ct), "CleanupOrphans", ct));
    }

    private static async Task RunSafe(Task task, string name, CancellationToken ct)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on leadership loss
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ReconciliationService: unhandled error in {Task}", name);
        }
    }
}
