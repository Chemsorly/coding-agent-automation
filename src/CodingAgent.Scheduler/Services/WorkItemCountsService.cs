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
/// Leader-gated — only one Scheduler replica registers measurements at a time.
/// </summary>
public sealed class WorkItemCountsService : BackgroundService
{
    private readonly ISchedulerApiClient _apiClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    private IEnumerable<Measurement<long>> _cachedMeasurements = [];

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

        // Register the gauge callback once at construction — same pattern as WorkItemMetricsBackgroundService.
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Expected on cancellation. */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemCountsService: failed to fetch counts — resetting to empty");
            Volatile.Write(ref _cachedMeasurements, []);
            WorkDistributionTelemetry.UpdateOldestPendingAge(null);
        }
    }
}
