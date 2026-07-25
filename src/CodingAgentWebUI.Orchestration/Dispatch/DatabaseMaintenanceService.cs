using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Periodic background service for database retention cleanup.
/// Runs in BOTH DB modes (K8s and SignalR) to ensure tables don't grow unbounded.
/// Cleans up: terminal WorkItems, PipelineRuns, and ConsolidationRuns past their retention period.
/// Gates all work behind leader election (when available) for multi-replica safety.
/// </summary>
public sealed class DatabaseMaintenanceService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatabaseMaintenanceService>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IConsolidationService _consolidationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ReconciliationServiceOptions _options;

    public DatabaseMaintenanceService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConsolidationService consolidationService,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _consolidationService = consolidationService;
        _serviceProvider = serviceProvider;
        _options = new ReconciliationServiceOptions();
        configuration.GetSection("WorkDistribution:Reconciliation").Bind(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("DatabaseMaintenanceService started — interval={Hours}h, workItem retention={WorkItemDays}d, " +
                        "pipelineRun retention={PipelineRunDays}d, consolidationRun retention={ConsolidationRunDays}d",
            _options.MaintenanceIntervalHours, _options.StaleRetentionDays,
            _options.PipelineRunRetentionDays, _options.ConsolidationRunRetentionDays);

        // Resolve ILeaderElectionService lazily — it's registered later in the DI pipeline
        // (K8s or SignalR mode branch) and may not be available at construction time.
        // TODO: ILeaderElectionService is resolved once here and cached for the service lifetime.
        // If it resolves to null (registration races with hosted service startup), subsequent cycles
        // will run WITHOUT leader gating. Consider re-resolving on each cycle or delaying the first
        // tick until ILeaderElectionService is available.
        var leaderElection = _serviceProvider.GetService(typeof(ILeaderElectionService)) as ILeaderElectionService;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.MaintenanceIntervalHours));

        // Immediate first tick (per project convention)
        await RunMaintenanceCycleAsync(leaderElection, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunMaintenanceCycleAsync(leaderElection, stoppingToken);
        }
    }

    private async Task RunMaintenanceCycleAsync(ILeaderElectionService? leaderElection, CancellationToken ct)
    {
        // Gate behind leader election if available (multi-replica safety)
        if (leaderElection is not null && !leaderElection.IsLeader)
        {
            Log.Debug("DatabaseMaintenanceService: skipping cycle — not the leader");
            return;
        }

        Log.Debug("DatabaseMaintenanceService: starting maintenance cycle");

        await CleanupStaleWorkItemsAsync(ct);
        await CleanupStalePipelineRunsAsync(ct);
        await CleanupStaleConsolidationRunsAsync(ct);
    }

    /// <summary>
    /// Terminal WorkItems older than retention period → DELETE (server-side).
    /// </summary>
    internal async Task CleanupStaleWorkItemsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.StaleRetentionDays);

            var deletedCount = await db.WorkItems
                .Where(w => (w.Status == WorkItemStatus.Succeeded ||
                             w.Status == WorkItemStatus.Failed ||
                             w.Status == WorkItemStatus.Cancelled) &&
                            w.CompletedAt != null &&
                            w.CompletedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deletedCount > 0)
            {
                Log.Information("DatabaseMaintenanceService: cleaned up {Count} stale work items (retention={Days}d)",
                    deletedCount, _options.StaleRetentionDays);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to cleanup stale work items (non-fatal)");
        }
    }

    /// <summary>
    /// PipelineRuns older than retention period → DELETE (server-side).
    /// </summary>
    internal async Task CleanupStalePipelineRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.PipelineRunRetentionDays);

            var deletedCount = await db.PipelineRuns
                .Where(r => r.CompletedAt != null && r.CompletedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deletedCount > 0)
            {
                Log.Information("DatabaseMaintenanceService: cleaned up {Count} stale pipeline runs (retention={Days}d)",
                    deletedCount, _options.PipelineRunRetentionDays);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to cleanup stale pipeline runs (non-fatal)");
        }
    }

    /// <summary>
    /// Terminal ConsolidationRuns older than retention period → DELETE via IConsolidationService.
    /// Uses client-side filtering because CompletedAtUtc is stored inside JSONB (no server-side filter).
    /// </summary>
    // TODO: GetRunHistoryAsync → LoadAllRunsAsync is bounded to Take(1000) ordered by Id DESC.
    // If >1000 consolidation runs exist, the oldest runs (most likely past retention) are excluded
    // from the result set and become unreachable by cleanup in a single pass. Consider adding a
    // dedicated unbounded cleanup query or paginated deletion for deployments with high run volume.
    internal async Task CleanupStaleConsolidationRunsAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.ConsolidationRunRetentionDays);
            var runs = await _consolidationService.GetRunHistoryAsync(ct);
            var deletedCount = 0;

            foreach (var run in runs)
            {
                if (ct.IsCancellationRequested) break;

                // Only delete terminal runs
                if (run.Status is not (ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Failed or ConsolidationRunStatus.Cancelled))
                    continue;

                // Use CompletedAtUtc if available, fall back to StartedAtUtc
                var anchor = run.CompletedAtUtc ?? run.StartedAtUtc;
                if (anchor >= cutoff)
                    continue;

                await _consolidationService.DeleteRunAsync(run.RunId, ct);
                deletedCount++;
            }

            if (deletedCount > 0)
            {
                Log.Information("DatabaseMaintenanceService: cleaned up {Count} stale consolidation runs (retention={Days}d)",
                    deletedCount, _options.ConsolidationRunRetentionDays);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to cleanup stale consolidation runs (non-fatal)");
        }
    }
}
