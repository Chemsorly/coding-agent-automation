using System.Diagnostics;
using System.Threading.RateLimiting;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Leader-elected background service that polls for Pending WorkItems and dispatches them via
/// <c>POST /api/work-items/{id}/dispatch</c>. This is the sole dispatcher following the
/// post-cutover teardown of the API-side dispatch loop (issue #2547).
///
/// <para>
/// Each poll cycle:
/// <list type="number">
///   <item>Fetches Pending WorkItems via <c>GET /api/work-items/pending</c>
///     (sorted by task type tier in <c>PipelineConfiguration.DispatchOrder</c>, by default
///     Triage &gt; Review &gt; Decomposition &gt; Implementation &gt; Consolidation,
///     then <c>PriorityWeight DESC</c> within a tier, then <c>CreatedAt ASC</c>).
///     The <c>maxResults</c> window (default 50) is filled strictly in tier order — if 50+
///     items of a higher tier are pending, lower-tier items are invisible to this cycle.</item>
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
/// Registered by default; <c>Scheduler:Dispatch:Enabled=false</c> turns it off for test hosts.
/// Registered in <see cref="CodingAgent.Scheduler.SchedulerServiceCollectionExtensions"/> when the flag is true.
/// </para>
/// </summary>
public sealed class WorkItemDispatchLoop : BackgroundService
{
    /// <summary>How many Pending items one cycle fetches (and how many dispatch calls may wait for a rate-limit token).</summary>
    internal const int FetchSize = 50;

    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private static readonly DispatchEligibilityEvaluator _eligibilityEvaluator = new();

    public WorkItemDispatchLoop(
        IPipelineApiWorkItemClient workItemClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        int rateLimitPerSecond = 10,
        TimeSpan? interval = null)
    {
        _workItemClient = workItemClient ?? throw new ArgumentNullException(nameof(workItemClient));
        ArgumentNullException.ThrowIfNull(logger);
        _leaderGate = leaderGate;
        _logger = logger.ForContext<WorkItemDispatchLoop>();
        _interval = interval ?? TimeSpan.FromSeconds(10);
        // Inline construction — RateLimiterFactory is internal to CodingAgent.Infrastructure.Common
        // and inaccessible from this assembly.
        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = rateLimitPerSecond,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            // Spec 056 DD9: a call waits for its token instead of aborting the cycle, so many quick hold answers do not
            // end a cycle early; the rate stays the same.
            QueueLimit = FetchSize,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = rateLimitPerSecond,
            AutoReplenishment = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "WorkItemDispatchLoop started — interval {Interval}, rateLimitPerSecond {RateLimit}",
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
                _logger.Debug("WorkItemDispatchLoop: skipping tick — not the leader");
                continue;
            }

            await PollAndDispatchAsync(stoppingToken);
        }
    }

    internal async Task PollAndDispatchAsync(CancellationToken ct)
    {
        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
        IReadOnlyList<CodingAgent.Pipeline.Models.PendingWorkItemDto> pending;
        try
        {
            pending = await _workItemClient.GetPendingAsync(maxResults: FetchSize, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemDispatchLoop: failed to fetch pending work items — will retry next interval");
            return;
        }

        if (pending.Count == 0)
        {
            // Still record the epoch even when idle so the DispatcherStalled alert fires only
            // when the loop itself stops running, not when the queue is simply empty.
            WorkDistributionTelemetry.RecordLastPollEpoch();
            return;
        }

        // Spec 056 DD9: the job templates and credentials stopped for this cycle. A hold stops only the work that resolves
        // to the same template (concurrency_limit, pvc_unavailable) or credential (credential_paused); routing holds stop
        // nothing. Resets on the next tick.
        var stopped = new CycleStops();

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested)
                break;

            // The stored selector and credential are the item's last resolution: an item never resolved (empty) is always
            // tried; a stale one costs this item one skipped cycle, never a wrong dispatch (the API resolves again).
            if (!_eligibilityEvaluator.EvaluateSelectorBlocked(item.AgentSelector, stopped.Templates).IsEligible
                || (item.Credential is { Length: > 0 } credential && stopped.Credentials.Contains(credential)))
            {
                _logger.Debug(
                    "WorkItemDispatchLoop: skipping {WorkItemId} — job template {AgentSelector} or credential {Credential} is stopped for this cycle",
                    item.Id, item.AgentSelector, item.Credential);
                continue;
            }

            if (!await DispatchItemAsync(item, stopped, ct))
                break;
        }

        // Record the epoch after each cycle (including cycles that were aborted mid-way).
        // The static call cannot be mocked by Moq — this is consistent with how WorkItemCountsService
        // calls WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback directly.
        WorkDistributionTelemetry.RecordLastPollEpoch();
    }

    /// <summary>
    /// Dispatches a single pending item. Returns <c>false</c> when the current cycle must be aborted.
    /// </summary>
    private async Task<bool> DispatchItemAsync(
        CodingAgent.Pipeline.Models.PendingWorkItemDto item, CycleStops stopped, CancellationToken ct)
    {
        // Acquire a rate-limit token before each dispatch call.
        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
        {
            _logger.Warning(
                "WorkItemDispatchLoop: rate limiter rejected lease for {WorkItemId} — aborting cycle",
                item.Id);
            return false;
        }

        DispatchPendingOutcome outcome;
        // Emit a Dispatch.Attempt span for each item dispatched. The span wraps the
        // dispatch call so the `result` tag is set before the span closes. Per-item only
        // (idle ticks return early at the pending.Count == 0 guard in PollAndDispatchAsync — no span emitted).
        using var dispatchActivity = PipelineTelemetry.ActivitySource.StartActivity("Dispatch.Attempt");
        dispatchActivity?.SetTag("work_item_id", item.Id);
        dispatchActivity?.SetTag("agent_selector", item.AgentSelector);
        try
        {
            outcome = await _workItemClient.DispatchPendingAsync(item.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Tag the span here: the result tag below is skipped on this path.
            dispatchActivity?.SetTag("result", "Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "WorkItemDispatchLoop: unexpected error dispatching {WorkItemId} — aborting cycle",
                item.Id);
            dispatchActivity?.SetTag("result", "Exception");
            return false;
        }

        dispatchActivity?.SetTag("result", outcome.Result.ToString());
        if (outcome.Reason is not null)
            dispatchActivity?.SetTag("reason", outcome.Reason);

        switch (outcome.Result)
        {
            case DispatchPendingResult.Dispatched:
                _logger.Debug("WorkItemDispatchLoop: dispatched {WorkItemId}", item.Id);
                break;

            case DispatchPendingResult.PermanentRejection:
                // The item is held (or no longer Pending). Spec 056 DD9: stop what the hold names.
                stopped.Apply(outcome, item);
                _logger.Debug(
                    "WorkItemDispatchLoop: {WorkItemId} held ({Reason}; job template {TemplateKey}, credential {Credential})",
                    item.Id, outcome.Reason, outcome.TemplateKey ?? item.AgentSelector, outcome.Credential ?? item.Credential);
                break;

            case DispatchPendingResult.Transient:
                // 503 — PVC unavailable, advisory lock timeout, or K8s failure.
                // Abort the entire cycle; retry on the next tick.
                _logger.Warning(
                    "WorkItemDispatchLoop: transient failure for {WorkItemId} — aborting cycle, will retry next interval",
                    item.Id);
                return false;

            case DispatchPendingResult.GlobalLimitReached:
                // Spec 057 req 3.5: the limit for all agents is reached, so no other item can start this cycle.
                // Expected backpressure, not a failure: Debug, and the next tick tries again.
                _logger.Debug("WorkItemDispatchLoop: all-agents limit reached — ending cycle");
                return false;
        }

        return true;
    }

    /// <summary>
    /// Spec 056 DD9: what one cycle stopped. <c>concurrency_limit</c> and <c>pvc_unavailable</c> stop the answer's job
    /// template (an earlier API names none: then the item's stored selector); <c>credential_paused</c> stops the answer's
    /// credential (or the stored one); every other hold stops nothing. An empty key is never stopped.
    /// </summary>
    internal sealed class CycleStops
    {
        public HashSet<string> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Credentials { get; } = new(StringComparer.Ordinal);

        public void Apply(DispatchPendingOutcome outcome, CodingAgent.Pipeline.Models.PendingWorkItemDto item)
        {
            switch (outcome.Reason)
            {
                case "concurrency_limit" or "pvc_unavailable":
                    AddIfSet(Templates, outcome.TemplateKey ?? item.AgentSelector);
                    break;
                case "credential_paused":
                    AddIfSet(Credentials, outcome.Credential ?? item.Credential);
                    break;
                case null:
                    // An answer without a reason: stop the stored selector, as before spec 056.
                    AddIfSet(Templates, item.AgentSelector);
                    break;
            }
        }

        private static void AddIfSet(HashSet<string> set, string? key)
        {
            if (!string.IsNullOrEmpty(key))
                set.Add(key);
        }
    }

    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }
}
