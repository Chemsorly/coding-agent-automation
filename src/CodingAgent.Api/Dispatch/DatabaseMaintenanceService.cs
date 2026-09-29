using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Serilog;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Periodic background service for database retention cleanup.
/// Cleans up terminal WorkItems and PipelineRuns past their retention period,
/// plus per-project count-based retention sweeps. Gates all work behind leader election
/// (when available) for multi-replica safety.
/// </summary>
public class DatabaseMaintenanceService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatabaseMaintenanceService>();

    // Protected so test subclasses can inject SQLite-compatible SQL overrides
    protected readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    // TODO(#9): _consolidationService is unused after CleanupStaleConsolidationRunsAsync was removed.
    // Remove this field and constructor parameter when IConsolidationService is fully dropped (sub-issue #9).
    // [WARNING] Until removed, verify IConsolidationService is registered as a singleton. DatabaseMaintenanceService
    // is a singleton (Spec 047); holding a scoped/transient IConsolidationService here would be a captured-dependency
    // bug where the scoped service (and any scoped DbContext it holds) lives for the singleton's lifetime.
    private readonly IConsolidationService _consolidationService;
    private readonly IPipelineRunHistoryService _pipelineRunHistoryService;
    private readonly DatabaseMaintenanceOptions _options;
    // Protected so test subclasses can inject SQLite-compatible SQL overrides
    protected readonly IPipelineConfigStore _configStore;

    public DatabaseMaintenanceService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConsolidationService consolidationService,
        IConfiguration configuration,
        IPipelineConfigStore configStore,
        IPipelineRunHistoryService? pipelineRunHistoryService = null)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        _dbFactory = dbFactory;
        _consolidationService = consolidationService;
        _pipelineRunHistoryService = pipelineRunHistoryService ?? NullPipelineRunHistoryService.Instance;
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
    /// Executes all sweep operations and returns a result with deletion/backfill counts.
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
        var retentionRuns = await RunSweepAsync(SweepPipelineRunRetentionAsync, "SweepPipelineRunRetention", ct);
        var retentionWi = await RunSweepAsync(SweepWorkItemRetentionAsync, "SweepWorkItemRetention", ct);
        var reconciled = await RunSweepAsync(ReconcileOrphanedPipelineRunsAsync, "ReconcileOrphanedPipelineRuns", ct);
        // TODO [WARNING]: The backfill runs here as part of the scheduled maintenance sweep, but the page's
        // data source has already switched to the pipeline-run read path as of this deployment. This means
        // there is a window between deploy and the first sweep tick where historical ConsolidationRuns rows
        // are absent from the Consolidation page (the "last run" cards and Run History appear empty/stale).
        // Consider triggering the backfill once on startup (e.g. via IHostedService) or documenting
        // that operators should manually trigger POST /api/scheduler/maintenance/retention-sweep immediately
        // after deploy to eliminate the data-visibility gap. (review-findings correctness)
        var backfilled = await RunSweepAsync(BackfillConsolidationRunsAsync, "BackfillConsolidationRuns", ct);
        return new RetentionSweepResult(staleWi, staleRuns, retentionRuns, retentionWi, reconciled, backfilled);
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
        int RetentionPipelineRunsDeleted,
        int RetentionWorkItemsDeleted,
        int OrphanedPipelineRunsReconciled = 0,
        int ConsolidationRunsBackfilled = 0);

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
    /// Backfills <c>CompletedAt = NOW()</c> for PipelineRun rows that have a terminal
    /// <c>FinalStep</c> (Completed=16, Failed=17, Cancelled=18) but a null <c>CompletedAt</c>.
    /// These ghost runs were produced by OCEs propagating out of post-PR steps before the
    /// <c>run.MarkCompleted()</c> call could execute. Without a CompletedAt value they accumulate
    /// permanently because all retention sweeps gate on <c>CompletedAt IS NOT NULL</c>.
    /// This method is idempotent and safe to call on every maintenance cycle.
    /// Returns the number of rows updated.
    /// </summary>
    public virtual async Task<int> ReconcileOrphanedPipelineRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // TODO: This load-all-then-update pattern differs from every other sweep in this service,
            // which uses ExecuteDeleteAsync / ExecuteUpdateAsync for set-based server-side SQL. For the
            // current 33 ghost runs this is harmless, but the method runs on every maintenance cycle.
            // If the forward fix (try-finally in PullRequestFinalizationService) regresses or the
            // separate terminal gap in QualityGateExecutor.RetryLoop produces orphans at scale, the
            // in-memory load could grow unbounded. Consider replacing with:
            //   await db.PipelineRuns
            //       .Where(r => r.CompletedAt == null && ...)
            //       .ExecuteUpdateAsync(s => s.SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow), ct);
            var orphans = await db.PipelineRuns
                .Where(r => r.CompletedAt == null &&
                            (r.FinalStep == PipelineStep.Completed ||
                             r.FinalStep == PipelineStep.Failed ||
                             r.FinalStep == PipelineStep.Cancelled))
                .ToListAsync(ct);

            if (orphans.Count == 0)
                return 0;

            var now = DateTimeOffset.UtcNow;
            foreach (var run in orphans)
                run.CompletedAt = now;

            await db.SaveChangesAsync(ct);

            Log.Warning(
                "DatabaseMaintenanceService: backfilled CompletedAt on {Count} orphaned PipelineRuns with terminal FinalStep and null CompletedAt",
                orphans.Count);

            return orphans.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // TODO: This swallows OperationCanceledException rather than re-throwing it, which is
            // inconsistent with every other sweep method in this class (e.g. CleanupStaleWorkItemsAsync,
            // SweepPipelineRunRetentionAsync) that all re-throw OCE and rely on RunSweepAsync to
            // propagate cancellation up to RunRetentionSweepAsync so that subsequent sweeps stop.
            // By swallowing OCE here, a cancellation that arrives during this sweep does not halt
            // the remaining sweeps — they continue running against an already-cancelled token.
            // The unit test (ReconcileOrphanedPipelineRuns_Cancellation_DoesNotThrow) validates this
            // swallowing behaviour but does not verify the downstream effect on sweep sequencing.
            // Consider re-throwing here (removing this catch block) so that RunSweepAsync propagates
            // cancellation consistently with all other sweeps.
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: ReconcileOrphanedPipelineRuns failed (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// Copies historical <c>ConsolidationRun</c> rows from the <c>ConsolidationRuns</c> table into
    /// <c>PipelineRuns</c> as proper <see cref="PipelineRunType.Consolidation"/> entries.
    /// Only terminal rows (Succeeded/Failed/Cancelled) are backfilled; non-terminal rows are skipped
    /// because they represent runs that are either in-flight (and will generate their own PipelineRun
    /// when they complete) or orphaned (handled by <see cref="IConsolidationService.CleanupOrphanedRunsAsync"/>).
    ///
    /// <para>
    /// <b>Identity key is <c>ConsolidationRun.RunId</c> (from the <c>Data</c> JSONB blob)</b>, NOT
    /// <c>ConsolidationRunEntity.Id</c> (the Postgres sequence PK). The two are different values.
    /// The idempotency check compares the deserialized <c>run.RunId</c> against existing
    /// <c>PipelineRuns.RunId</c> values — using <c>entity.Id</c> instead would silently produce
    /// duplicate rows on every sweep cycle.
    /// </para>
    /// </summary>
    /// <returns>The number of rows newly backfilled.</returns>
    internal async Task<int> BackfillConsolidationRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Step 1: load all ConsolidationRunEntity rows and deserialize their Data blobs.
            var consolidationEntities = await db.ConsolidationRuns.AsNoTracking().ToListAsync(ct);

            if (consolidationEntities.Count == 0)
                return 0;

            // Step 2: load existing PipelineRun RunIds for consolidation type — for the idempotency check.
            var existingIds = (await db.PipelineRuns
                .AsNoTracking()
                .Where(r => r.RunType == PipelineRunType.Consolidation)
                .Select(r => r.RunId)
                .ToListAsync(ct))
                .ToHashSet();

            var backfilledCount = 0;

            foreach (var entity in consolidationEntities)
            {
                if (ct.IsCancellationRequested)
                    break;

                // CRITICAL: use RunId from the Data JSONB blob, NOT entity.Id.
                // ConsolidationRunEntity.Id is a Postgres-generated sequence PK that has no
                // relationship to the logical run ID. The actual run identity is ConsolidationRun.RunId
                // embedded in the Data blob.
                if (string.IsNullOrEmpty(entity.Data))
                {
                    Log.Warning("DatabaseMaintenanceService: BackfillConsolidationRuns — entity {Id} has null/empty Data blob, skipping", entity.Id);
                    continue;
                }

                ConsolidationRun run;
                try
                {
                    run = JsonSerializer.Deserialize<ConsolidationRun>(entity.Data, PipelineJsonOptions.Default)!;
                }
                catch (JsonException ex)
                {
                    Log.Warning(ex, "DatabaseMaintenanceService: BackfillConsolidationRuns — entity {Id} Data blob is not valid JSON, skipping", entity.Id);
                    continue;
                }

                if (run is null || string.IsNullOrEmpty(run.RunId))
                {
                    Log.Warning("DatabaseMaintenanceService: BackfillConsolidationRuns — entity {Id} deserialized to null or has empty RunId, skipping", entity.Id);
                    continue;
                }

                // Skip non-terminal runs: they are either in-flight or orphaned.
                if (run.Status is not (ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Failed or ConsolidationRunStatus.Cancelled))
                    continue;

                // Idempotency: skip if already present in PipelineRuns.
                if (!Guid.TryParse(run.RunId, out var runGuid))
                {
                    Log.Warning("DatabaseMaintenanceService: BackfillConsolidationRuns — run {RunId} has non-GUID RunId, skipping", run.RunId);
                    continue;
                }

                if (existingIds.Contains(runGuid))
                    continue;

                // Map ConsolidationRunStatus → PipelineStep.
                var finalStep = run.Status switch
                {
                    ConsolidationRunStatus.Succeeded => PipelineStep.Completed,
                    ConsolidationRunStatus.Failed => PipelineStep.Failed,
                    ConsolidationRunStatus.Cancelled => PipelineStep.Cancelled,
                    _ => PipelineStep.Failed  // unreachable: non-terminal runs are filtered above
                };

                // Construct the deterministic IssueIdentifier — matches the format used in
                // ConsolidationService.TriggerAsync: "{type}:{templateId}" or "{type}:global".
                var issueIdentifier = !string.IsNullOrEmpty(run.TemplateId)
                    ? $"{run.Type}:{run.TemplateId}"
                    : $"{run.Type}:global";

                // WorkItemId: parse from the ConsolidationRun's WorkItemId field (may be null for old rows).
                Guid? workItemId = Guid.TryParse(run.WorkItemId, out var wid) ? wid : null;

                var summary = new PipelineRunSummary
                {
                    RunId = run.RunId,
                    IssueIdentifier = issueIdentifier,
                    IssueTitle = run.TemplateName ?? run.Type.ToString(),
                    FinalStep = finalStep,
                    StartedAtOffset = run.StartedAtUtc,
                    CompletedAtOffset = run.CompletedAtUtc,
                    RunType = PipelineRunType.Consolidation,
                    InitiatedBy = ConsolidationConstants.InitiatedBy,
                    ConsolidationType = run.Type,
                    ConsolidationTemplateId = run.TemplateId,
                    ConsolidationTemplateName = run.TemplateName,
                    ConsolidationResultSummary = run.Summary,
                    ProjectId = run.ProjectId,
                    ProjectName = run.ProjectName,
                    WorkItemId = workItemId
                };

                await _pipelineRunHistoryService.AddRunSummaryAsync(summary, ct);
                // TODO [WARNING]: If AddRunSummaryAsync throws a transient exception, the outer catch at the
                // bottom of this method will swallow it and return the count accumulated so far. The un-persisted
                // row is NOT added to existingIds (the Add below only runs if this line succeeds), so it will be
                // retried on the next sweep — no permanent data loss. However, backfilledCount is only incremented
                // after AddRunSummaryAsync returns successfully, so the reported count accurately reflects only
                // successfully written rows. If the outer catch fires mid-loop, the remaining un-processed rows
                // are also silently skipped until the next sweep. This is acceptable for a maintenance sweep but
                // consider per-item try/catch with logging if higher reliability is required. (DotNetSpecialist review)
                existingIds.Add(runGuid); // prevent double-insertion within same sweep
                backfilledCount++;
            }

            if (backfilledCount > 0)
                Log.Information("DatabaseMaintenanceService: BackfillConsolidationRuns — backfilled {Count} rows into PipelineRuns", backfilledCount);

            return backfilledCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DatabaseMaintenanceService: BackfillConsolidationRuns failed (non-fatal)");
            return 0;
        }
    }

    /// <summary>
    /// No-op implementation of <see cref="IPipelineRunHistoryService"/> used when no real service
    /// is injected (e.g. existing tests that construct <see cref="DatabaseMaintenanceService"/>
    /// without the new parameter).
    /// </summary>
    private sealed class NullPipelineRunHistoryService : IPipelineRunHistoryService
    {
        public static readonly NullPipelineRunHistoryService Instance = new();

        public void TryDeleteWorkspace(WorkspacePath? workspacePath, string runId, string workspaceBaseDirectory) { }
        public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
        public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PipelineRunSummary>>([]);
        public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default) => Task.FromResult(new PagedResult<PipelineRunSummary> { Items = [], Page = page, PageSize = pageSize, HasMore = false });
        public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default) => Task.FromResult(new PagedResult<PipelineRunSummary> { Items = [], Page = page, PageSize = pageSize, HasMore = false });
        public Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<PipelineRunSummary?>(null);
    }
}
