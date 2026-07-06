using System.Diagnostics.Metrics;
using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Telemetry;

/// <summary>
/// Periodically queries WorkItems grouped by (Status, AgentSelector) and caches measurements
/// for the <c>workdistribution.workitems_by_status</c> observable gauge.
/// Replaces the inline System.Threading.Timer pattern from Program.cs with a proper
/// BackgroundService that supports cancellation and participates in host lifecycle ordering.
/// </summary>
public sealed class WorkItemMetricsBackgroundService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WorkItemMetricsBackgroundService>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    // TODO: Consider marking volatile — field is written by the background service thread and read
    // by the OTEL metrics collection thread. Reference assignment is atomic on x64 but volatile would
    // make cross-thread intent explicit and guarantee correctness on ARM (store reordering).
    private IEnumerable<Measurement<long>> _cachedMeasurements = [];

    public WorkItemMetricsBackgroundService(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull(dbFactory) to fail fast on DI misconfiguration
        // instead of deferring failure to the first ExecuteAsync tick.
        _dbFactory = dbFactory;

        // TODO: RegisterWorkItemsByStatusCallback overwrites the static callback — if a second instance
        // is constructed (e.g., in tests or DI misconfiguration), the previous callback is silently lost.
        // Consider adding a guard or logging a warning on duplicate registration.
        WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(() => _cachedMeasurements);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("WorkItemMetricsBackgroundService started — polling every 10s");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        // Immediate first tick
        await UpdateMeasurementsAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await UpdateMeasurementsAsync(stoppingToken);
        }
    }

    private async Task UpdateMeasurementsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var counts = await db.WorkItems
                .GroupBy(w => new { w.Status, w.AgentSelector })
                .Select(g => new { g.Key.Status, g.Key.AgentSelector, Count = g.LongCount() })
                .ToListAsync(ct);
            _cachedMeasurements = counts.Select(c => new Measurement<long>(c.Count,
                new KeyValuePair<string, object?>("status", c.Status.ToString()),
                new KeyValuePair<string, object?>("agent_selector", c.AgentSelector)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WorkItemMetricsBackgroundService: failed to query work item counts, resetting to empty");
            _cachedMeasurements = [];
        }
    }
}
