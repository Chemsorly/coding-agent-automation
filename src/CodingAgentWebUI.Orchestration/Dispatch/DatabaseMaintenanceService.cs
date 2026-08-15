using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
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
/// Cleans up:
/// - Terminal WorkItems, PipelineRuns, and ConsolidationRuns past their age-based retention period.
/// - PipelineRuns and terminal WorkItems beyond the per-project count-based retention limit.
/// Gates all work behind leader election (when available) for multi-replica safety.
/// </summary>
public sealed class DatabaseMaintenanceService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatabaseMaintenanceService>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IConsolidationService _consolidationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ReconciliationServiceOptions _options;
    private readonly IPipelineConfigStore _pipelineConfigStore;

    public DatabaseMaintenanceService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConsolidationService consolidationService,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IPipelineConfigStore pipelineConfigStore)
    {
        // TODO [WARNING]: Add ArgumentNullException.ThrowIfNull(pipelineConfigStore) here to match
        // the null-guard pattern used in other services (ConsolidationDispatchService, etc.).
        // A null IPipelineConfigStore is currently stored silently and only fails at runtime
        // during the first sweep with a NullReferenceException instead of a startup-time error.
        _dbFactory = dbFactory;
        _consolidationService = consolidationService;
        _serviceProvider = serviceProvider;
        _pipelineConfigStore = pipelineConfigStore;
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
        var leaderElection = _serviceProvider.GetService(typeof(ILeaderElectionService)) as ILeaderElectionService;

        // Age-based maintenance timer (driven by appsettings ReconciliationServiceOptions)
        using var maintenanceTimer = new PeriodicTimer(TimeSpan.FromHours(_options.MaintenanceIntervalHours));

        // TODO [WARNING]: DbRetentionSweepInterval from PipelineConfiguration is never read; the retention timer
        // is hardcoded to 24h regardless of the user-configured value. Per the issue spec the interval "takes
        // effect on restart", so it should be read here. The safe fix is to read it from a startup LoadPipelineConfigAsync
        // call (with fallback to 24h on failure) rather than the raw IConfiguration binding, since
        // DbRetentionSweepInterval lives in the Pipeline config store, not in appsettings.
        using var retentionTimer = new PeriodicTimer(TimeSpan.FromHours(24));

        // Run both cycles immediately on startup, then on their respective periodic schedules.
        var maintenanceTask = RunMaintenanceLoopAsync(maintenanceTimer, leaderElection, stoppingToken);
        var retentionTask = RunRetentionLoopAsync(retentionTimer, leaderElection, stoppingToken);

        await Task.WhenAll(maintenanceTask, retentionTask);
    }

    private async Task RunMaintenanceLoopAsync(
        PeriodicTimer timer,
        ILeaderElectionService? leaderElection,
        CancellationToken stoppingToken)
    {
        // Immediate first tick
        await RunMaintenanceCycleAsync(leaderElection, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunMaintenanceCycleAsync(leaderElection, stoppingToken);
        }
    }

    private async Task RunRetentionLoopAsync(
        PeriodicTimer timer,
        ILeaderElectionService? leaderElection,
        CancellationToken stoppingToken)
    {
        // Immediate first tick
        await RunRetentionSweepAsync(leaderElection, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunRetentionSweepAsync(leaderElection, stoppingToken);
        }
    }

    private async Task RunMaintenanceCycleAsync(ILeaderElectionService? leaderElection, CancellationToken ct)
    {
        // Gate behind leader election if available (multi-replica safety)
        if (leaderElection is not null && !leaderElection.IsLeader)
        {
            Log.Debug("DatabaseMaintenanceService: skipping maintenance cycle — not the leader");
            return;
        }

        Log.Debug("DatabaseMaintenanceService: starting maintenance cycle");

        await CleanupStaleWorkItemsAsync(ct);
        await CleanupStalePipelineRunsAsync(ct);
        await CleanupStaleConsolidationRunsAsync(ct);
    }

    internal async Task RunRetentionSweepAsync(ILeaderElectionService? leaderElection, CancellationToken ct)
    {
        // Gate behind leader election if available (multi-replica safety)
        if (leaderElection is not null && !leaderElection.IsLeader)
        {
            Log.Debug("DatabaseMaintenanceService: skipping retention sweep — not the leader");
            return;
        }

        PipelineConfiguration config;
        try
        {
            config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to load pipeline config for retention sweep (non-fatal)");
            return;
        }

        // Skip entirely if both counts are disabled — avoid touching the DB at all
        if (config.PipelineRunRetentionCount == -1 && config.WorkItemRetentionCount == -1)
        {
            Log.Debug("DatabaseMaintenanceService: retention sweep skipped — both counts are -1 (disabled)");
            return;
        }

        Log.Debug("DatabaseMaintenanceService: starting retention sweep — pipelineRun={PipelineRunCount}, workItem={WorkItemCount}",
            config.PipelineRunRetentionCount, config.WorkItemRetentionCount);

        // Run per-table sweeps independently: a failure on one must not block the other
        if (config.PipelineRunRetentionCount != -1)
            await PruneOldPipelineRunsAsync(config.PipelineRunRetentionCount, ct);

        if (config.WorkItemRetentionCount != -1)
            await PruneOldWorkItemsAsync(config.WorkItemRetentionCount, ct);
    }

    /// <summary>
    /// Deletes PipelineRuns rows ranked beyond <paramref name="retentionCount"/> per project
    /// (most recent by StartedAt, tiebreak by RunId). Rows with ProjectId IS NULL are never deleted.
    /// </summary>
    /// <remarks>
    /// Uses a Postgres window-function DELETE…USING pattern. ExecuteSqlAsync does not support the
    /// EF Core InMemory provider — cannot be unit-tested without a real Postgres instance.
    /// The partial index IX_PipelineRuns_ProjectId_StartedAt (added in migration
    /// 20260815000000_AddRetentionIndexes) covers this query to avoid full sequential scans.
    /// </remarks>
    internal async Task PruneOldPipelineRunsAsync(int retentionCount, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // TODO [WARNING]: ExecuteSqlAsync resolves to the FormattableString overload, which parameterizes
            // {retentionCount} as a DbParameter — this is safe and not SQL injection. Do NOT refactor to
            // ExecuteSqlRawAsync with this interpolated string; that overload takes a plain string and would
            // concatenate retentionCount directly into SQL.
            var deletedCount = await db.Database.ExecuteSqlAsync(
                $"""
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
                  ) ranked
                  WHERE rn > {retentionCount}
                ) to_delete
                WHERE "PipelineRuns"."RunId" = to_delete."RunId"
                  AND "PipelineRuns"."ProjectId" IS NOT NULL
                """,
                ct);

            if (deletedCount > 0)
            {
                Log.Information(
                    "DatabaseMaintenanceService: retention sweep deleted {Count} PipelineRuns rows (retentionCount={RetentionCount})",
                    deletedCount, retentionCount);
                WorkDistributionTelemetry.PipelineRunsDeleted.Add(deletedCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to prune old PipelineRuns (non-fatal)");
        }
    }

    /// <summary>
    /// Deletes terminal WorkItems rows (Status IN (3,4,5), CompletedAt IS NOT NULL) ranked beyond
    /// <paramref name="retentionCount"/> per project (most recent by CompletedAt, tiebreak by Id).
    /// Non-terminal rows and terminal rows with CompletedAt IS NULL are never touched.
    /// Rows with ProjectId IS NULL are never deleted.
    /// </summary>
    /// <remarks>
    /// Uses a Postgres window-function DELETE…USING pattern. ExecuteSqlAsync does not support the
    /// EF Core InMemory provider — cannot be unit-tested without a real Postgres instance.
    /// The partial index IX_WorkItems_ProjectId_CompletedAt_Terminal (added in migration
    /// 20260815000000_AddRetentionIndexes) covers this query to avoid full sequential scans.
    /// </remarks>
    internal async Task PruneOldWorkItemsAsync(int retentionCount, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // TODO [WARNING]: ExecuteSqlAsync resolves to the FormattableString overload, which parameterizes
            // {retentionCount} as a DbParameter — this is safe and not SQL injection. Do NOT refactor to
            // ExecuteSqlRawAsync with this interpolated string; that overload takes a plain string and would
            // concatenate retentionCount directly into SQL.
            var deletedCount = await db.Database.ExecuteSqlAsync(
                $"""
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
                  WHERE rn > {retentionCount}
                ) to_delete
                WHERE "WorkItems"."Id" = to_delete."Id"
                  AND "WorkItems"."ProjectId" IS NOT NULL
                  AND "WorkItems"."Status" IN (3, 4, 5)
                  AND "WorkItems"."CompletedAt" IS NOT NULL
                """,
                ct);

            if (deletedCount > 0)
            {
                Log.Information(
                    "DatabaseMaintenanceService: retention sweep deleted {Count} WorkItems rows (retentionCount={RetentionCount})",
                    deletedCount, retentionCount);
                WorkDistributionTelemetry.WorkItemsDeleted.Add(deletedCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to prune old WorkItems (non-fatal)");
        }
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

            // TODO [WARNING]: WorkItems rows with ProjectId IS NULL are not guarded here, unlike
            // CleanupStalePipelineRunsAsync which has r.ProjectId != null in its Where predicate.
            // The issue requirement states "Rows with ProjectId IS NULL are never deleted" for both
            // tables. Add w.ProjectId != null && to this predicate to apply the same protection.
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
    /// Rows with ProjectId IS NULL (consolidation runs, legacy rows) are never deleted.
    /// </summary>
    internal async Task CleanupStalePipelineRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.PipelineRunRetentionDays);

            var deletedCount = await db.PipelineRuns
                .Where(r => r.ProjectId != null &&        // Never delete ProjectId IS NULL rows
                            r.CompletedAt != null &&
                            r.CompletedAt < cutoff)
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
