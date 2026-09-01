using System.Diagnostics.Metrics;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Telemetry;
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
    private readonly TimeSpan _pollInterval;

    private IEnumerable<Measurement<long>> _cachedMeasurements = [];

    /// <param name="dbFactory">Factory used to open DB connections on each poll tick.</param>
    /// <param name="pollInterval">
    ///   How often to poll. Defaults to 10 seconds in production.
    ///   Inject a shorter value in tests to avoid multi-second waits.
    /// </param>
    public WorkItemMetricsBackgroundService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);

        WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(() => Volatile.Read(ref _cachedMeasurements));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("WorkItemMetricsBackgroundService started — polling every {Interval}", _pollInterval);

        using var timer = new PeriodicTimer(_pollInterval);

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
            Volatile.Write(ref _cachedMeasurements, counts.Select(c => new Measurement<long>(c.Count,
                new KeyValuePair<string, object?>("status", c.Status.ToString()),
                new KeyValuePair<string, object?>("agent_selector", c.AgentSelector))));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WorkItemMetricsBackgroundService: failed to query work item counts, resetting to empty");
            Volatile.Write(ref _cachedMeasurements, Enumerable.Empty<Measurement<long>>());
        }
    }
}
