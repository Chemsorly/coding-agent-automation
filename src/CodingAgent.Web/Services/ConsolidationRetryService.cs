using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Background service that periodically retries consolidation runs stuck in the
/// <see cref="ConsolidationRunStatus.Queued"/> state due to transient dispatch failures
/// (e.g. concurrency limit 409, PVC unavailable 503).
///
/// <para>
/// Before the permanent/transient failure split was introduced, the only recovery path
/// for a Queued consolidation run was orchestrator restart (startup rehydration via
/// <see cref="ConsolidationRehydrationExtensions"/>). This service adds a bounded
/// periodic sweep so a correctly-configured run that hit a transient capacity failure
/// can recover without a pod restart.
/// </para>
///
/// <para>
/// Safety: a run that has already been cascaded to <c>Failed</c> (permanent failure —
/// no job template) is terminal (<see cref="ConsolidationRunStatus.Failed"/> is a
/// terminal status) and is excluded by <see cref="IConsolidationService.RehydrateQueuedRunsAsync"/>
/// which filters to <c>Status == Queued</c> only. There is therefore no risk of
/// re-dispatching a permanently-failed run.
/// </para>
/// </summary>
internal sealed class ConsolidationRetryService : BackgroundService
{
    /// <summary>Default interval between retry sweeps.</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly IConsolidationService _consolidationService;
    private readonly IConsolidationDispatcher _consolidationDispatcher;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    public ConsolidationRetryService(
        IConsolidationService consolidationService,
        IConsolidationDispatcher consolidationDispatcher,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _consolidationService = consolidationService;
        _consolidationDispatcher = consolidationDispatcher;
        _logger = logger.ForContext<ConsolidationRetryService>();
        _interval = interval ?? DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "ConsolidationRetryService started — sweeping for queued consolidation runs every {Interval}",
            _interval);

        using var timer = new PeriodicTimer(_interval);

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

            await RunRetrySweepAsync(stoppingToken);
        }

        _logger.Information("ConsolidationRetryService stopped.");
    }

    /// <summary>
    /// Fetches all currently-Queued consolidation runs and attempts to dispatch each one.
    /// Each dispatch failure is handled inside <see cref="IConsolidationDispatcher.DispatchRunAsync"/>
    /// (which swallows errors and logs) — this method never throws.
    /// </summary>
    internal async Task RunRetrySweepAsync(CancellationToken ct)
    {
        IReadOnlyList<Pipeline.Models.ConsolidationRun> queuedRuns;
        try
        {
            queuedRuns = await _consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ConsolidationRetryService: failed to load queued runs — skipping sweep");
            return;
        }

        if (queuedRuns.Count == 0)
            return;

        _logger.Information(
            "ConsolidationRetryService: retrying {Count} queued consolidation run(s)",
            queuedRuns.Count);

        foreach (var run in queuedRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // DispatchRunAsync swallows all errors from ConsolidationDispatcher in normal
                // operation — but guard here as well so a programming error (e.g. mock throw in
                // tests) never kills the sweep loop or propagates to the caller.
                await _consolidationDispatcher.DispatchRunAsync(run, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "ConsolidationRetryService: unexpected error dispatching run {RunId} — skipping",
                    run.RunId);
            }
        }
    }
}
