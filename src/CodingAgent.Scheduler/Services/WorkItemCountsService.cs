using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;
using System.Diagnostics.Metrics;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Replaces WorkItemMetricsBackgroundService (which had a direct EF dependency).
/// Polls GET /api/work-items/counts-by-status every 10 seconds and feeds the
/// WorkDistributionTelemetry.workitems_by_status observable gauge.
/// Also polls GET /api/agents and GET /api/agents/credential-pool to feed the
/// agent and credential-pool gauges on PipelineTelemetry.Meter and WorkDistributionTelemetry.Meter.
/// Leader-gated — only one Scheduler replica registers measurements at a time.
/// </summary>
public sealed class WorkItemCountsService : BackgroundService
{
    private readonly ISchedulerApiClient _apiClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    private IEnumerable<Measurement<long>> _cachedMeasurements = [];

    // Agent gauge cache fields — updated on each successful leader poll.
    // TODO [WARNING]: _cachedAgentActive and _cachedAgentTotal are plain (non-volatile) int fields written
    // on the poll thread and read on the OTel collection thread via the observeActive/observeTotal closures.
    // On ARM (where the Scheduler runs in production) the CPU's store-buffer means the collection thread can
    // observe _agentMetricsPolled == true while still reading stale values for the two ints (the same hazard
    // that motivated Volatile.Write in WorkDistributionTelemetry.UpdateCredentialPoolMetrics).
    // Fix: use Volatile.Write(ref _cachedAgentActive, ...) / Volatile.Write(ref _cachedAgentTotal, ...) before
    // the _agentMetricsPolled = true write, and Volatile.Read in the observe callbacks, mirroring
    // the _credentialPoolAvailable pattern and the _cachedMeasurements Volatile.Read/Write already in this file.
    private int _cachedAgentActive;
    private int _cachedAgentTotal;
    // Sentinel: false until the first successful poll completes (mirrors _credentialPoolUpdated pattern).
    // Also reset to false on poll failure so gauges emit nothing after a failure.
    // TODO [WARNING]: _agentMetricsPolled is a plain (non-volatile) bool. The write on the poll thread and
    // the read in the shouldEmit closure on the OTel collection thread are not paired with a release/acquire
    // fence. This means the sentinel flip can become visible before the cached int values it guards (same
    // class of defect fixed for _credentialPoolUpdated). Apply Volatile.Write(ref _agentMetricsPolled, ...)
    // and Volatile.Read(ref _agentMetricsPolled) in the shouldEmit lambda, matching the volatile pattern
    // used by _credentialPoolUpdated (declared volatile) and _cachedMeasurements (Volatile.Read/Write).
    private bool _agentMetricsPolled;

    public WorkItemCountsService(
        ISchedulerApiClient apiClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _apiClient = apiClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<WorkItemCountsService>();
        _interval = interval ?? TimeSpan.FromSeconds(10);

        // Register the workitems_by_status gauge callback once at construction — same pattern as WorkItemMetricsBackgroundService.
        // The closure checks IsLeader at read time so that a non-leader replica emits no measurements
        // even if _cachedMeasurements still holds data from a previous leadership term.
        // When _leaderGate is null (single-replica / no leader election), the replica always emits.
        // Only the first-registered callback wins (Interlocked.CompareExchange guard in
        // RegisterWorkItemsByStatusCallback), which is correct: regardless of which replica registered
        // first, its callback will return empty measurements when that replica is not the leader.
        WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(
            () => _leaderGate is null || _leaderGate.IsLeader
                ? Volatile.Read(ref _cachedMeasurements)
                : Enumerable.Empty<Measurement<long>>());

        // Register the agent gauge callbacks (agent.jobs.active and agent.connections.total).
        // shouldEmit gates on leadership AND _agentMetricsPolled:
        //   - non-leader → always silent
        //   - leader, before first successful poll → silent (no stale 0s)
        //   - leader, after failed poll → silent (_agentMetricsPolled reset to false on failure)
        //   - leader, after successful poll → emits cached values
        PipelineTelemetry.RegisterAgentGaugeCallbacks(
            observeActive: () => _cachedAgentActive,
            observeTotal: () => _cachedAgentTotal,
            shouldEmit: () => (_leaderGate is null || _leaderGate.IsLeader) && _agentMetricsPolled);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("WorkItemCountsService started — polling every {Interval}", _interval);

        // Immediate first poll
        await UpdateMeasurementsAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await UpdateMeasurementsAsync(stoppingToken);
        }
    }

    private async Task UpdateMeasurementsAsync(CancellationToken ct)
    {
        // Only the leader polls — one source of metrics truth
        if (_leaderGate is { IsLeader: false }) return;

        try
        {
            var response = await _apiClient.GetWorkItemCountsAsync(ct);
            Volatile.Write(ref _cachedMeasurements,
                response.Counts.Select(c => new Measurement<long>(c.Count,
                    new KeyValuePair<string, object?>("status", c.Status),
                    new KeyValuePair<string, object?>("agent_selector", c.AgentSelector)))
                               .ToList());
            // TODO [WARNING]: No test verifies that UpdateOldestPendingAge is called with
            // response.OldestPendingCreatedAt on the success path, nor with null on the exception
            // path (the catch block below). The gauge's observeValues conversion (ms > 0 → age in
            // seconds; empty measurement when 0) is also entirely untested. Add a MeterListener-based
            // test that seeds a non-null OldestPendingCreatedAt, ticks the poller, and asserts the
            // gauge emits a plausible positive age; and one that asserts no measurement is emitted
            // when the API returns null (no Pending items) or on the error path.
            WorkDistributionTelemetry.UpdateOldestPendingAge(response.OldestPendingCreatedAt);

            // Poll agent counts and feed the agent.jobs.active / agent.connections.total gauges.
            // Null-guard handles the Moq loose-mode Task.FromResult(null) case in tests.
            var agentCounts = await _apiClient.GetAgentCountsAsync(ct);
            if (agentCounts is not null)
            {
                _cachedAgentActive = agentCounts.Busy;
                _cachedAgentTotal = agentCounts.Total;
            }
            // TODO [WARNING]: If agentCounts is null (API returned HTTP 200 with a null body — a bug on
            // the API side), the cache is silently left at its prior values and _agentMetricsPolled is still
            // set to true below, causing the gauges to emit stale values with no warning. Consider logging
            // a warning when agentCounts is null (the same way the credential-pool path treats poolStatus).

            // Poll credential pool and feed the workdistribution.credential_pool_* gauges.
            var poolStatus = await _apiClient.GetAgentCredentialPoolAsync(ct);
            if (poolStatus is not null)
            {
                WorkDistributionTelemetry.UpdateCredentialPoolMetrics(poolStatus.Available, poolStatus.Claimed);
            }

            // Mark agent metrics as polled — enables the shouldEmit callback to return true.
            _agentMetricsPolled = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Expected on cancellation. */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemCountsService: failed to fetch counts — resetting to empty");
            Volatile.Write(ref _cachedMeasurements, []);
            WorkDistributionTelemetry.UpdateOldestPendingAge(null);
            // Reset agent metrics sentinel so gauges emit nothing until the next successful poll.
            _agentMetricsPolled = false;
            // Reset credential pool sentinel so pool gauges also emit nothing after failure.
            WorkDistributionTelemetry.ResetCredentialPoolMetrics();
        }
    }
}
