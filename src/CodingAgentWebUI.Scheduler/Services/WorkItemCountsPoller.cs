using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;
using System.Diagnostics.Metrics;

namespace CodingAgentWebUI.Scheduler.Services;

/// <summary>
/// Replaces WorkItemMetricsBackgroundService (which had a direct EF dependency).
/// Polls GET /api/work-items/counts-by-status every 10 seconds and feeds the
/// WorkDistributionTelemetry.workitems_by_status observable gauge.
/// Leader-gated — only one Scheduler replica registers measurements at a time.
/// </summary>
public sealed class WorkItemCountsPoller : BackgroundService
{
    private readonly ISchedulerApiClient _apiClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    private IEnumerable<Measurement<long>> _cachedMeasurements = [];

    public WorkItemCountsPoller(
        ISchedulerApiClient apiClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _apiClient = apiClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<WorkItemCountsPoller>();
        _interval = interval ?? TimeSpan.FromSeconds(10);

        // Register the gauge callback once at construction — same pattern as WorkItemMetricsBackgroundService.
        WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(
            () => Volatile.Read(ref _cachedMeasurements));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("WorkItemCountsPoller started — polling every {Interval}", _interval);

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
            var counts = await _apiClient.GetWorkItemCountsAsync(ct);
            Volatile.Write(ref _cachedMeasurements,
                counts.Select(c => new Measurement<long>(c.Count,
                    new KeyValuePair<string, object?>("status", c.Status),
                    new KeyValuePair<string, object?>("agent_selector", c.AgentSelector)))
                      .ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemCountsPoller: failed to fetch counts — resetting to empty");
            Volatile.Write(ref _cachedMeasurements, []);
        }
    }
}
