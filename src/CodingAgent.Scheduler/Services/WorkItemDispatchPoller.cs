using System.Threading.RateLimiting;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Leader-elected background service that polls for Pending WorkItems and dispatches them via
/// <c>POST /api/work-items/{id}/dispatch</c>. Intended to become the sole dispatcher once the
/// API-side <c>WorkItemDispatchService</c> is disabled via <c>WorkDistribution:Dispatch:Enabled=false</c>.
///
/// <para>
/// Each poll cycle:
/// <list type="number">
///   <item>Fetches Pending non-consolidation WorkItems via <c>GET /api/work-items/pending</c>
///     (sorted <c>PriorityWeight DESC, CreatedAt ASC</c>, excludes consolidation items).</item>
///   <item>Dispatches each item sequentially so the endpoint's per-call snapshot stays accurate.</item>
///   <item>Applies rate limiting (token bucket, default 10/s) before each dispatch call.</item>
///   <item>Per-selector stop: if <c>POST /{id}/dispatch</c> returns 409 for a given
///     <c>AgentSelector</c>, that selector is blocked for the remainder of the current cycle
///     (conservative; resets next tick).</item>
///   <item>Cycle abort on 503: a transient failure aborts the current cycle; the next tick retries.</item>
///   <item>Records the last poll epoch via <see cref="WorkDistributionTelemetry.RecordLastPollEpoch"/>
///     so the <c>workdistribution_dispatcher_last_poll_epoch_seconds</c> gauge has a live emitter.</item>
/// </list>
/// </para>
/// <para>
/// Enabled/disabled via <c>Scheduler:Dispatch:Enabled</c> (default <c>false</c>).
/// Registered in <see cref="CodingAgent.Scheduler.SchedulerServiceCollectionExtensions"/> when the flag is true.
/// </para>
/// </summary>
public sealed class WorkItemDispatchPoller : BackgroundService
{
    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public WorkItemDispatchPoller(
        IPipelineApiWorkItemClient workItemClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        int rateLimitPerSecond = 10,
        TimeSpan? interval = null)
    {
        _workItemClient = workItemClient ?? throw new ArgumentNullException(nameof(workItemClient));
        ArgumentNullException.ThrowIfNull(logger);
        _leaderGate = leaderGate;
        _logger = logger.ForContext<WorkItemDispatchPoller>();
        _interval = interval ?? TimeSpan.FromSeconds(10);
        // Inline construction — RateLimiterFactory is internal to CodingAgent.Infrastructure.Common
        // and inaccessible from this assembly.
        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = rateLimitPerSecond,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = rateLimitPerSecond,
            AutoReplenishment = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "WorkItemDispatchPoller started — interval {Interval}, rateLimitPerSecond {RateLimit}",
            _interval, _rateLimiter.GetStatistics()?.CurrentAvailablePermits);

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

            // Only the leader Scheduler replica dispatches items.
            if (_leaderGate is { IsLeader: false })
            {
                _logger.Debug("WorkItemDispatchPoller: skipping tick — not the leader");
                continue;
            }

            await PollAndDispatchAsync(stoppingToken);
        }
    }

    internal async Task PollAndDispatchAsync(CancellationToken ct)
    {
        IReadOnlyList<CodingAgent.Pipeline.Models.PendingWorkItemDto> pending;
        try
        {
            pending = await _workItemClient.GetPendingAsync(maxResults: 50, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemDispatchPoller: failed to fetch pending work items — will retry next interval");
            return;
        }

        if (pending.Count == 0)
        {
            // Still record the epoch even when idle so the DispatcherStalled alert fires only
            // when the poller itself stops running, not when the queue is simply empty.
            WorkDistributionTelemetry.RecordLastPollEpoch();
            return;
        }

        // Per-cycle set of selectors that have hit a permanent 409.
        // Conservative: any 409 for a selector blocks all remaining items with that selector
        // for this cycle. Resets on the next tick.
        var stoppedSelectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested)
                break;

            if (stoppedSelectors.Contains(item.AgentSelector))
            {
                _logger.Debug(
                    "WorkItemDispatchPoller: skipping {WorkItemId} — selector {AgentSelector} is stopped for this cycle",
                    item.Id, item.AgentSelector);
                continue;
            }

            // Acquire a rate-limit token before each dispatch call.
            using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
            if (!lease.IsAcquired)
            {
                _logger.Warning(
                    "WorkItemDispatchPoller: rate limiter rejected lease for {WorkItemId} — aborting cycle",
                    item.Id);
                break;
            }

            DispatchPendingResult result;
            try
            {
                result = await _workItemClient.DispatchPendingAsync(item.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "WorkItemDispatchPoller: unexpected error dispatching {WorkItemId} — aborting cycle",
                    item.Id);
                break;
            }

            switch (result)
            {
                case DispatchPendingResult.Dispatched:
                    _logger.Debug("WorkItemDispatchPoller: dispatched {WorkItemId}", item.Id);
                    break;

                case DispatchPendingResult.PermanentRejection:
                    // 409 — item not Pending, concurrency limit, or no template.
                    // Stop dispatching all items with this selector for the current cycle.
                    stoppedSelectors.Add(item.AgentSelector);
                    _logger.Debug(
                        "WorkItemDispatchPoller: permanent rejection for {WorkItemId} (selector {AgentSelector}) — "
                        + "selector blocked for this cycle",
                        item.Id, item.AgentSelector);
                    break;

                case DispatchPendingResult.Transient:
                    // 503 — PVC unavailable, advisory lock timeout, or K8s failure.
                    // Abort the entire cycle; retry on the next tick.
                    _logger.Warning(
                        "WorkItemDispatchPoller: transient failure for {WorkItemId} — aborting cycle, will retry next interval",
                        item.Id);
                    goto exitLoop;
            }
        }

    exitLoop:
        // Record the epoch after each cycle (including cycles that were aborted mid-way).
        // The static call cannot be mocked by Moq — this is consistent with how WorkItemCountsPoller
        // calls WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback directly.
        WorkDistributionTelemetry.RecordLastPollEpoch();
    }

    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }
}
