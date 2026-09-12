using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Background service that periodically retries Queued consolidation runs whose synchronous
/// dispatch previously returned a transient failure (409 concurrency limit or 503 PVC unavailable).
///
/// <para>
/// Without this service, a transient dispatch failure leaves the run in <c>Queued</c> status until
/// the next orchestrator restart triggers startup rehydration — which may be minutes to hours later.
/// This service closes that gap by sweeping for Queued runs every <see cref="RetryInterval"/> and
/// re-submitting them via <see cref="IConsolidationDispatcher"/>.
/// </para>
///
/// <para>
/// The retry count is bounded by <see cref="MaxRetryAttempts"/>. Once a run has been dispatched
/// that many times without leaving the Queued state (i.e. dispatch keeps returning transient
/// failures), it is cascaded to <c>Failed</c> via <see cref="IConsolidationService.UpdateRunAsync"/>
/// so it surfaces in the Attention view rather than spinning in Queued silently.
/// </para>
///
/// <para>
/// This service handles the transient-failure case. Permanent failures (422 — no job template
/// for the selector) are cascaded to <c>Failed</c> immediately by
/// <see cref="ConsolidationDispatcher"/> and never reach this service's retry counter.
/// </para>
/// </summary>
internal sealed class ConsolidationTransientRetryService : BackgroundService
{
    // TODO: Remove this static dead field — it is never read; all log calls use the instance
    // field _logger below. The static field is initialised at class load time via the global
    // Serilog.Log static, which (a) fires before DI is configured and (b) bypasses the injected
    // mock logger in tests, creating a latent risk if any code path accidentally references Log
    // instead of _logger. See review-findings.md [WARNING] ConsolidationTransientRetryService.cs:34.
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationTransientRetryService>();

    private readonly IConsolidationService _consolidationService;
    private readonly IConsolidationDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// How often to sweep for Queued runs. Default: 2 minutes.
    /// Set to a shorter value in tests via object initializer.
    /// </summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum number of times a single run will be re-dispatched before the service gives up
    /// and cascades it to <c>Failed</c>. Default: 10.
    /// Set to a smaller value in tests via object initializer.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 10;

    // In-memory retry counter keyed by RunId. Runs that are no longer Queued (succeeded or were
    // cascaded) are absent from the map on the next tick and do not accumulate entries — the counter
    // is only incremented for runs that are still present in the Queued list on that tick.
    private readonly Dictionary<string, int> _attemptCounts = new(StringComparer.Ordinal);

    public ConsolidationTransientRetryService(
        IConsolidationService consolidationService,
        IConsolidationDispatcher dispatcher,
        TimeProvider clock,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _consolidationService = consolidationService;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger.ForContext<ConsolidationTransientRetryService>();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "ConsolidationTransientRetryService started — sweeping every {Interval} (max {Max} attempts per run)",
            RetryInterval, MaxRetryAttempts);

        // TODO: The do/while loop fires the first sweep immediately on service start, before
        // ConsolidationRehydrationExtensions.RehydrateAsync (which also calls IConsolidationDispatcher
        // for all Queued runs) has had a chance to complete. Both paths call DispatchRunAsync for the
        // same runs concurrently. The endpoint concurrency guard returns 409, which is treated as a
        // transient failure and increments the retry counter by 1 unnecessarily on startup. This
        // will not cause data corruption (the run stays Queued) but may exhaust the retry budget one
        // attempt earlier than expected. Consider adding a startup delay (e.g. TimeSpan.FromSeconds(30))
        // or making the first tick fire after the first WaitForNextTickAsync instead of immediately.
        // See review-findings.md [WARNING] ConsolidationTransientRetryService.cs:92.
        using var timer = new PeriodicTimer(RetryInterval, _clock);

        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await SafeWaitForNextTickAsync(timer, stoppingToken));

        _logger.Information("ConsolidationTransientRetryService stopped.");
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        IReadOnlyList<ConsolidationRun> queuedRuns;
        try
        {
            queuedRuns = await _consolidationService.RehydrateQueuedRunsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ConsolidationTransientRetryService: RehydrateQueuedRunsAsync failed — sweep skipped, will retry on next tick");
            return;
        }

        if (queuedRuns.Count == 0)
            return;

        // Prune stale entries from the counter: runs no longer in the Queued list have either
        // succeeded or been cascaded and should not hold memory indefinitely.
        var activeRunIds = new HashSet<string>(queuedRuns.Select(r => r.RunId), StringComparer.Ordinal);
        var stale = _attemptCounts.Keys.Where(id => !activeRunIds.Contains(id)).ToList();
        foreach (var id in stale)
            _attemptCounts.Remove(id);

        foreach (var run in queuedRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            _attemptCounts.TryGetValue(run.RunId, out var previousAttempts);

            if (previousAttempts >= MaxRetryAttempts)
            {
                // Retry budget exhausted — cascade to Failed so the run surfaces in Attention.
                _logger.Warning(
                    "ConsolidationTransientRetryService: run {RunId} ({Type}) has been dispatched {Count} times " +
                    "without leaving Queued status — retry limit ({Max}) exceeded. Cascading to Failed.",
                    run.RunId, run.Type, previousAttempts, MaxRetryAttempts);

                try
                {
                    await _consolidationService.UpdateRunAsync(
                        new RunId(run.RunId),
                        ConsolidationRunStatus.Failed,
                        $"Transient dispatch retry limit exceeded after {previousAttempts} retry attempts. " +
                        "Operator action required: check agent capacity, PVC availability, and job template configuration.",
                        CancellationToken.None); // terminal write — must not be abandoned on shutdown
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "ConsolidationTransientRetryService: failed to cascade run {RunId} to Failed status — " +
                        "will retry cascade on the next sweep tick",
                        run.RunId);
                }

                // TODO: _attemptCounts.Remove is called unconditionally after a cascade attempt,
                // even if UpdateRunAsync threw above. On the next tick the run is still in the
                // Queued list (it was never transitioned), previousAttempts is 0 (key absent),
                // and the service dispatches it again — restarting the full 10-attempt cycle.
                // If UpdateRunAsync consistently fails (e.g. persistent DB error), the run is
                // re-dispatched indefinitely in MaxRetryAttempts-sized windows rather than
                // converging. Fix: on cascade failure, re-insert the counter at MaxRetryAttempts
                // (not 0) so the next tick retries the cascade instead of dispatching again:
                //   catch (Exception ex) { ...; _attemptCounts[run.RunId] = MaxRetryAttempts; }
                // See review-findings.md [WARNING] ConsolidationTransientRetryService.cs:156.
                // Remove from counter after cascade attempt so the counter does not keep growing
                // and re-cascade on every subsequent tick.
                _attemptCounts.Remove(run.RunId);
                continue;
            }

            // Increment first so that even if dispatch throws the attempt is counted.
            _attemptCounts[run.RunId] = previousAttempts + 1;

            _logger.Debug(
                "ConsolidationTransientRetryService: dispatching run {RunId} ({Type}), attempt {Attempt}/{Max}",
                run.RunId, run.Type, previousAttempts + 1, MaxRetryAttempts);

            try
            {
                await _dispatcher.DispatchRunAsync(run, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // ConsolidationDispatcher swallows errors and logs them, so this catch is a
                // defensive belt-and-suspenders guard. Log and continue to the next run.
                _logger.Warning(ex,
                    "ConsolidationTransientRetryService: unexpected error dispatching run {RunId} ({Type})",
                    run.RunId, run.Type);
            }
        }
    }

    private static async ValueTask<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
