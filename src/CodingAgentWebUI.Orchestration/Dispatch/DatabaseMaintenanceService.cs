using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Telemetry;
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
/// Cleans up terminal WorkItems, PipelineRuns, and ConsolidationRuns past their retention period,
/// plus per-project count-based retention sweeps. Gates all work behind leader election
/// (when available) for multi-replica safety.
/// </summary>
public class DatabaseMaintenanceService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatabaseMaintenanceService>();

    // Protected so test subclasses can inject SQLite-compatible SQL overrides
    protected readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IConsolidationService _consolidationService;
    private readonly DatabaseMaintenanceOptions _options;
    // Protected so test subclasses can inject SQLite-compatible SQL overrides
    protected readonly IPipelineConfigStore _configStore;

    public DatabaseMaintenanceService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConsolidationService consolidationService,
        IConfiguration configuration,
        IPipelineConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        _dbFactory = dbFactory;
        _consolidationService = consolidationService;
        _options = new DatabaseMaintenanceOptions();
        configuration.GetSection("WorkDistribution:Reconciliation").Bind(_options);
        _configStore = configStore;
    }

    // ExecuteAsync is intentionally absent.
    // Spec 047: DatabaseMaintenanceService is registered as a plain singleton (not AddHostedService).
    // Sweeps are triggered exclusively by the Scheduler via POST /api/scheduler/maintenance/retention-sweep
    // → RunRetentionSweepAsync. The PeriodicTimer-based timer path was removed to prevent accidental
    // re-activation: adding AddHostedService back would run uncounted sweeps alongside the
    // Scheduler-triggered path with no leader-gate coordination between them.

    /// <summary>
    /// Executes all six sweep operations and returns a result with deletion counts.
    /// Used by the Scheduler's POST /api/scheduler/maintenance/retention-sweep endpoint.
    /// Callers are responsible for leader-gate checks before calling this method.
    /// </summary>
    public async Task<RetentionSweepResult> RunRetentionSweepAsync(CancellationToken ct)
    {
        // Each sweep is individually fault-isolated at the orchestrator level: a failure in one sweep
        // is logged and returns 0, allowing the remaining sweeps to proceed. Each individual sweep
        // method also has its own internal try/catch for fine-grained error handling; this outer
        // per-call guard ensures a fault that escapes an individual sweep (e.g. from a subclass
        // override in tests, or a missing catch in future code) cannot prevent later sweeps from running.
        var staleWi = await RunSweepAsync(CleanupStaleWorkItemsAsync, "CleanupStaleWorkItems", ct);
        var staleRuns = await RunSweepAsync(CleanupStalePipelineRunsAsync, "CleanupStalePipelineRuns", ct);
        var staleConsolidation = await RunSweepAsync(CleanupStaleConsolidationRunsAsync, "CleanupStaleConsolidationRuns", ct);
        var retentionRuns = await RunSweepAsync(SweepPipelineRunRetentionAsync, "SweepPipelineRunRetention", ct);
        var retentionWi = await RunSweepAsync(SweepWorkItemRetentionAsync, "SweepWorkItemRetention", ct);
        // NOTE: The reconciliation count is discarded here and not included in RetentionSweepResult.
        // Callers (e.g. the Scheduler API endpoint and its metrics) cannot observe how many ghost rows
        // were backfilled. Consider adding an OrphanedPipelineRunsReconciled field to RetentionSweepResult
        // and capturing the return value: var reconciled = await RunSweepAsync(...).
        await RunSweepAsync(ReconcileOrphanedPipelineRunsAsync, "ReconcileOrphanedPipelineRuns", ct);
        return new RetentionSweepResult(staleWi, staleRuns, staleConsolidation, retentionRuns, retentionWi);
    }

    private static async Task<int> RunSweepAsync(Func<CancellationToken, Task<int>> sweep, string name, CancellationToken ct)
    {
        try
        {
            return await sweep(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: sweep {Name} failed (non-fatal, continuing)", name);
            return 0;
        }
    }

    /// <summary>Counts and result for a full retention sweep.</summary>
    public sealed record RetentionSweepResult(
        int StaleWorkItemsDeleted,
        int StalePipelineRunsDeleted,
        int StaleConsolidationRunsDeleted,
        int RetentionPipelineRunsDeleted,
        int RetentionWorkItemsDeleted);

    /// <summary>
    /// Terminal WorkItems older than retention period → DELETE (server-side).
    /// Returns the number of rows deleted.
    /// </summary>
    internal async Task<int> CleanupStaleWorkItemsAsync(CancellationToken ct)
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
            return deletedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to cleanup stale work items (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// PipelineRuns older than retention period → DELETE (server-side).
    /// Returns the number of rows deleted.
    /// </summary>
    internal async Task<int> CleanupStalePipelineRunsAsync(CancellationToken ct)
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
            return deletedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to cleanup stale pipeline runs (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// Terminal ConsolidationRuns older than retention period → DELETE via IConsolidationService.
    /// Returns the number of runs deleted.
    /// </summary>
    // Note: GetRunHistoryAsync → LoadAllRunsAsync is bounded to Take(1000) ordered by Id DESC.
    internal async Task<int> CleanupStaleConsolidationRunsAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.ConsolidationRunRetentionDays);
            var runs = await _consolidationService.GetRunHistoryAsync(ct);
            var deletedCount = 0;

            foreach (var run in runs)
            {
                if (ct.IsCancellationRequested) break;

                if (run.Status is not (ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Failed or ConsolidationRunStatus.Cancelled))
                    continue;

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
            return deletedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: failed to cleanup stale consolidation runs (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// Per-project count-based retention sweep for <c>PipelineRuns</c>.
    /// Returns the number of rows deleted.
    /// </summary>
    internal virtual async Task<int> SweepPipelineRunRetentionAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configStore.LoadPipelineConfigAsync(ct);
            var retentionCount = config.PipelineRunRetentionCount;

            if (retentionCount == -1)
                return 0; // Disabled

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
            return deletedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: PipelineRuns retention sweep failed (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// Per-project count-based retention sweep for terminal <c>WorkItems</c>.
    /// Returns the number of rows deleted.
    /// </summary>
    internal virtual async Task<int> SweepWorkItemRetentionAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configStore.LoadPipelineConfigAsync(ct);
            var retentionCount = config.WorkItemRetentionCount;

            if (retentionCount == -1)
                return 0; // Disabled

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
            return deletedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: WorkItems retention sweep failed (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// Backfills <c>CompletedAt = NOW()</c> for ghost <c>PipelineRuns</c> that have a terminal
    /// <c>FinalStep</c> (Completed=16, Failed=17, Cancelled=18) but a null <c>CompletedAt</c>.
    /// These rows arise when an <see cref="OperationCanceledException"/> from
    /// <c>RunPostPrSequenceAsync</c> skipped the <c>run.MarkCompleted()</c> call before the
    /// fix introduced in issue #2316.
    /// Returns the number of rows updated.
    /// </summary>
    internal virtual async Task<int> ReconcileOrphanedPipelineRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // FinalStep integer values: Completed=16, Failed=17, Cancelled=18
            // (PipelineStep enum — stored as int in Postgres).
            // NOTE: These integer literals are hardcoded rather than derived from the PipelineStep enum
            // (e.g. (int)PipelineStep.Completed). If a future terminal state is added to the enum, this
            // SQL and both test overrides (RetentionSweepIntegrationTests, DatabaseMaintenanceServiceAdditionalTests)
            // must be updated manually — there is no compile-time link. Consider deriving the values from
            // the enum or adding a unit test asserting (int)PipelineStep.Completed==16, Failed==17, Cancelled==18
            // so a reorder breaks the build.
            const string sql = """
                UPDATE "PipelineRuns"
                SET "CompletedAt" = NOW()
                WHERE "FinalStep" IN (16, 17, 18)
                  AND "CompletedAt" IS NULL
                """;

            var updatedCount = await db.Database.ExecuteSqlRawAsync(sql, ct);

            if (updatedCount > 0)
            {
                Log.Information(
                    "DatabaseMaintenanceService: reconciled {Count} orphaned PipelineRuns (terminal FinalStep, null CompletedAt)",
                    updatedCount);
            }
            return updatedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: orphaned PipelineRuns reconciliation failed (non-fatal)");
            return 0;
        }
    }
}
