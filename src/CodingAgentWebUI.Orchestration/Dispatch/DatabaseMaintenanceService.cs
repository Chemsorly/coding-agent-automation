using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Periodic background service for database retention cleanup.
/// Runs in BOTH DB modes (K8s and SignalR) to ensure tables don't grow unbounded.
/// Cleans up: terminal WorkItems, PipelineRuns, and ConsolidationRuns past their retention period,
/// plus per-project count-based retention sweeps for PipelineRuns and WorkItems.
/// Gates all work behind leader election (when available) for multi-replica safety.
/// </summary>
public class DatabaseMaintenanceService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatabaseMaintenanceService>();

    // Protected so test subclasses can inject SQLite-compatible SQL overrides
    protected readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IConsolidationService _consolidationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ReconciliationServiceOptions _options;
    // Protected so test subclasses can inject SQLite-compatible SQL overrides
    protected readonly IPipelineConfigStore _configStore;

    public DatabaseMaintenanceService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConsolidationService consolidationService,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IPipelineConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        _dbFactory = dbFactory;
        _consolidationService = consolidationService;
        _serviceProvider = serviceProvider;
        _options = new ReconciliationServiceOptions();
        configuration.GetSection("WorkDistribution:Reconciliation").Bind(_options);
        _configStore = configStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read DbRetentionSweepInterval from pipeline config store.
        // This replaces MaintenanceIntervalHours as the PeriodicTimer period.
        // Falls back to 24h default if config cannot be read on startup.
        var sweepInterval = TimeSpan.FromHours(24);
        try
        {
            var config = await _configStore.LoadPipelineConfigAsync(stoppingToken);
            sweepInterval = config.DbRetentionSweepInterval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex,
                "DatabaseMaintenanceService: failed to read DbRetentionSweepInterval from config store — using default {Default}",
                sweepInterval);
        }

        Log.Information(
            "DatabaseMaintenanceService started — sweepInterval={SweepInterval}, " +
            "workItem retention={WorkItemDays}d, pipelineRun retention={PipelineRunDays}d, " +
            "consolidationRun retention={ConsolidationRunDays}d",
            sweepInterval, _options.StaleRetentionDays,
            _options.PipelineRunRetentionDays, _options.ConsolidationRunRetentionDays);

        // Resolve ILeaderElectionService lazily — it's registered later in the DI pipeline
        // (K8s or SignalR mode branch) and may not be available at construction time.
        var leaderElection = _serviceProvider.GetService(typeof(ILeaderElectionService)) as ILeaderElectionService;

        using var timer = new PeriodicTimer(sweepInterval);

        // Immediate first tick (per project convention)
        await RunMaintenanceCycleAsync(leaderElection, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunMaintenanceCycleAsync(leaderElection, stoppingToken);
        }
    }

    protected virtual async Task RunMaintenanceCycleAsync(ILeaderElectionService? leaderElection, CancellationToken ct)
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
        await SweepPipelineRunRetentionAsync(ct);
        await SweepWorkItemRetentionAsync(ct);
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
    // Note: GetRunHistoryAsync → LoadAllRunsAsync is bounded to Take(1000) ordered by Id DESC.
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

    /// <summary>
    /// Per-project count-based retention sweep for <c>PipelineRuns</c>.
    /// Deletes the oldest completed runs beyond <see cref="PipelineConfiguration.PipelineRunRetentionCount"/>
    /// per project. Rows with <c>ProjectId IS NULL</c> or <c>CompletedAt IS NULL</c> (active runs)
    /// are never deleted.
    /// </summary>
    // Note: SweepPipelineRunRetentionAsync and SweepWorkItemRetentionAsync each call
    // LoadPipelineConfigAsync independently, resulting in two config-store round-trips per maintenance
    // cycle. If the store issues a DB query on each call (no in-memory cache), this doubles the load
    // for no correctness benefit. Consider loading config once in RunMaintenanceCycleAsync and passing
    // the counts as parameters, consistent with how _options is used by the CleanupStale* methods.
    internal virtual async Task SweepPipelineRunRetentionAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configStore.LoadPipelineConfigAsync(ct);
            var retentionCount = config.PipelineRunRetentionCount;

            if (retentionCount == -1)
                return; // Disabled

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Removes completed runs ranked beyond N per project (ordered newest-first by StartedAt).
            // Only completed runs (CompletedAt IS NOT NULL) are eligible — active in-progress
            // runs must never be deleted regardless of per-project count.
            // ProjectId IS NULL rows (consolidation runs, legacy rows) are always exempt.
            // WorkItemStatus ordinal cross-reference: Succeeded=3, Failed=4, Cancelled=5
            // (PipelineRunEntity has no Status column — CompletedAt IS NOT NULL is the terminal proxy)
            const string sql = """
                DELETE FROM "PipelineRuns"
                USING (
                  SELECT "RunId"
                  FROM (
                    SELECT "RunId",
                           ROW_NUMBER() OVER (
                             PARTITION BY "ProjectId"
                             ORDER BY "StartedAt" DESC, "RunId" DESC
                           ) AS rn
                    FROM "PipelineRuns"
                    WHERE "ProjectId" IS NOT NULL
                      AND "CompletedAt" IS NOT NULL
                  ) ranked
                  WHERE rn > @retentionCount
                ) to_delete
                WHERE "PipelineRuns"."RunId" = to_delete."RunId"
                  AND "PipelineRuns"."ProjectId" IS NOT NULL
                  AND "PipelineRuns"."CompletedAt" IS NOT NULL
                """;

            var deletedCount = await db.Database.ExecuteSqlRawAsync(
                sql,
                new[] { new NpgsqlParameter("retentionCount", retentionCount) },
                ct);

            if (deletedCount > 0)
            {
                Log.Information(
                    "DatabaseMaintenanceService: retention sweep deleted {Count} PipelineRuns rows (retentionCount={N} per project)",
                    deletedCount, retentionCount);
                WorkDistributionTelemetry.DbRetentionPipelineRunsDeleted.Add(deletedCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: PipelineRuns retention sweep failed (non-fatal)");
        }
    }

    /// <summary>
    /// Per-project count-based retention sweep for terminal <c>WorkItems</c>.
    /// Deletes the oldest terminal rows beyond <see cref="PipelineConfiguration.WorkItemRetentionCount"/>
    /// per project. Only rows with <c>Status IN (3,4,5)</c> AND <c>CompletedAt IS NOT NULL</c>
    /// AND <c>ProjectId IS NOT NULL</c> are eligible. Non-terminal rows and rows with
    /// <c>CompletedAt IS NULL</c> are never deleted.
    /// </summary>
    internal virtual async Task SweepWorkItemRetentionAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configStore.LoadPipelineConfigAsync(ct);
            var retentionCount = config.WorkItemRetentionCount;

            if (retentionCount == -1)
                return; // Disabled

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Removes terminal WorkItems rows ranked beyond N per project (ordered newest-first).
            // Only terminal rows (Succeeded/Failed/Cancelled) with a non-null CompletedAt are eligible.
            // Rows with a null ProjectId are always exempt.
            const string sql = """
                DELETE FROM "WorkItems"
                USING (
                  SELECT "Id"
                  FROM (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                             PARTITION BY "ProjectId"
                             ORDER BY "CompletedAt" DESC, "Id" DESC
                           ) AS rn
                    FROM "WorkItems"
                    WHERE "ProjectId" IS NOT NULL
                      AND "Status" IN (3, 4, 5)
                      AND "CompletedAt" IS NOT NULL
                  ) ranked
                  WHERE rn > @retentionCount
                ) to_delete
                WHERE "WorkItems"."Id" = to_delete."Id"
                  AND "WorkItems"."ProjectId" IS NOT NULL
                  AND "WorkItems"."Status" IN (3, 4, 5)
                  AND "WorkItems"."CompletedAt" IS NOT NULL
                """;

            var deletedCount = await db.Database.ExecuteSqlRawAsync(
                sql,
                new[] { new NpgsqlParameter("retentionCount", retentionCount) },
                ct);

            if (deletedCount > 0)
            {
                Log.Information(
                    "DatabaseMaintenanceService: retention sweep deleted {Count} WorkItems rows (retentionCount={N} per project)",
                    deletedCount, retentionCount);
                WorkDistributionTelemetry.DbRetentionWorkItemsDeleted.Add(deletedCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: WorkItems retention sweep failed (non-fatal)");
        }
    }
}
