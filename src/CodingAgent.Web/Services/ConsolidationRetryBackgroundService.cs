using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Background service that periodically retries queued consolidation runs whose synchronous
/// dispatch failed transiently (capacity limit / PVC unavailable).
///
/// <para>
/// <b>Why this exists:</b> <see cref="ConsolidationDispatcher"/> dispatches runs synchronously
/// on the <see cref="IWorkDistributor"/> path. When the dispatch returns a transient failure
/// (409 concurrency / 503 PVC), the run is left <c>Queued</c> and retried only at the next
/// orchestrator restart. An orchestrator pod that stays alive for hours will never re-attempt
/// such a run, leaving it stuck <c>Queued</c> forever — the same symptom the issue #2536
/// permanent-failure fix addressed for mis-routed runs.
/// </para>
///
/// <para>
/// <b>Bounded retry guarantee:</b> This service does NOT retry indefinitely. It re-dispatches
/// queued runs on a fixed interval (default 2 minutes) up to
/// <see cref="MaxConsecutiveFailures"/> consecutive failures per run before giving up and leaving
/// the run in <c>Queued</c> state. A permanent failure (422) returned by the dispatcher is
/// handled inside <see cref="ConsolidationDispatcher"/> — it cascades the run to
/// <c>Failed</c> directly, so this service will not see it on the next tick.
/// </para>
///
/// <para>
/// <b>Design constraints:</b>
/// <list type="bullet">
///   <item>Does NOT replace startup rehydration — <see cref="ConsolidationRehydrationExtensions"/>
///         still handles cleanup and first-dispatch on pod start.</item>
///   <item>Does NOT change the in-process concurrency guard in <see cref="ConsolidationService"/>
///         — that guard prevents double-dispatch of the same (type, templateId) pair.</item>
///   <item>Safe across replicas: the concurrency-limit check in the API prevents duplicate
///         execution; a second replica dispatching the same run produces a 409 capacity
///         failure which is a no-op for the run (stays Queued).</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConsolidationRetryBackgroundService : BackgroundService
{
    /// <summary>
    /// How often to poll for queued runs. Conservative default: 2 minutes between sweeps
    /// so a capacity burst does not trigger a tight retry loop.
    /// </summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Stop logging per-run retry warnings after this many consecutive failures (prevents log spam
    /// when the cluster is persistently over-capacity).
    /// </summary>
    // TODO [WARNING]: MaxConsecutiveFailures is declared but never referenced in the implementation.
    // RetryQueuedRunsAsync retries every queued run unconditionally on every sweep tick with no
    // per-run failure counter — contrary to the "bounded retry" guarantee documented above.
    // A run whose DispatchRunAsync always throws an unexpected infrastructure exception will be
    // retried indefinitely. Either implement per-run failure tracking using this constant, or
    // remove the constant and update the class doc to state retries are unbounded (acceptable
    // because permanent dispatch failures are already cascaded to Failed by ConsolidationDispatcher).
    // (review-findings.md DotNetSpecialist warning and TestQualityReviewer SUGGESTION)
    public const int MaxConsecutiveFailures = 5;

    private readonly IConsolidationService _consolidationService;
    private readonly IConsolidationDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _retryInterval;

    public ConsolidationRetryBackgroundService(
        IConsolidationService consolidationService,
        IConsolidationDispatcher dispatcher,
        TimeProvider clock,
        ILogger logger,
        TimeSpan? retryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _consolidationService = consolidationService;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger.ForContext<ConsolidationRetryBackgroundService>();
        _retryInterval = retryInterval ?? DefaultRetryInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "ConsolidationRetryBackgroundService started — retrying queued runs every {Interval}",
            _retryInterval);

        using var timer = new PeriodicTimer(_retryInterval, _clock);

        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RetryQueuedRunsAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Loads all queued consolidation runs and attempts to dispatch each one.
    /// Errors per run are swallowed so one bad run does not block the others.
    /// </summary>
    internal async Task RetryQueuedRunsAsync(CancellationToken ct)
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
            _logger.Warning(ex, "ConsolidationRetryBackgroundService: failed to load queued runs — skipping sweep");
            return;
        }

        if (queuedRuns.Count == 0)
            return;

        _logger.Information(
            "ConsolidationRetryBackgroundService: retry sweep found {Count} queued run(s)",
            queuedRuns.Count);

        foreach (var run in queuedRuns)
        {
            if (ct.IsCancellationRequested)
                break;

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
                // DispatchRunAsync is documented to never throw for dispatch errors —
                // this catch guards against unexpected infrastructure failures.
                _logger.Warning(ex,
                    "ConsolidationRetryBackgroundService: unexpected error retrying run {RunId} ({Type}). " +
                    "Run remains Queued; will retry on the next sweep.",
                    run.RunId, run.Type);
            }
        }
    }
}
