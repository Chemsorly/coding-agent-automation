using System.Text.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IPipelineRunHistoryService"/>.
/// Persists completed run summaries to the PipelineRuns table with a JSONB SummaryJson column
/// for lossless round-trip of all <see cref="PipelineRunSummary"/> fields.
/// Indexed columns: StartedAt (desc), AgentId, (FinalStep + CompletedAt) composite.
/// </summary>
public sealed class PostgresPipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILogger _logger;

    /// <summary>Maximum number of run summaries returned by <see cref="GetRunHistoryAsync"/>.</summary>
    internal const int MaxHistorySize = 1000;

    /// <summary>Default page size for paginated queries.</summary>
    internal const int DefaultPageSize = 50;

    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    public PostgresPipelineRunHistoryService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILogger logger)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Defense-in-depth: ensure terminal CurrentStep before persisting to history.
        // Non-terminal steps indicate a mid-pipeline state that should never be the final persisted value.
        PipelineStep? finalStepOverride = null;
        if (!run.CurrentStep.IsTerminal())
        {
            _logger.Warning(
                "AddRunToHistoryAsync: run {RunId} has non-terminal CurrentStep={Step}, forcing to Failed",
                run.RunId, run.CurrentStep);
            finalStepOverride = PipelineStep.Failed;
        }

        var summary = run.ToSummary(finalStepOverride);

        try
        {
            await AddRunToHistoryInternalAsync(summary, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist run summary {RunId} to database", summary.RunId);
        }
    }

    /// <inheritdoc />
    public async Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        try
        {
            await AddRunToHistoryInternalAsync(summary, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist run summary {RunId} to database via direct-summary path", summary.RunId);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
    {
        return await GetRunHistoryInternalAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxHistorySize);
        if ((long)(page - 1) * pageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(page), page,
                $"Value would cause overflow when computing the page offset. Maximum page for pageSize={pageSize} is {int.MaxValue / pageSize + 1}.");

        return await GetRunHistoryPagedInternalAsync(page, pageSize, finalStep: null, projectId: null, since: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxHistorySize);

        if (!feedbackOnly)
            return await GetRunHistoryAsync(page, pageSize, ct).ConfigureAwait(false);

        return await GetRunHistoryPagedWithFeedbackFilterInternalAsync(page, pageSize, finalStep: null, projectId: null, since: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, CancellationToken ct = default)
    {
        // No filters → defer to the existing feedback/plain paths (unchanged, incl. the offset overflow guard).
        if (finalStep is null && string.IsNullOrEmpty(projectId))
            return await GetRunHistoryAsync(page, pageSize, feedbackOnly, ct).ConfigureAwait(false);

        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxHistorySize);

        // FinalStep and ProjectId are mapped columns, so both filters run DB-side before paging —
        // pagination stays correct across the whole history. Feedback (JSONB) is combined in the same query.
        return feedbackOnly
            ? await GetRunHistoryPagedWithFeedbackFilterInternalAsync(page, pageSize, finalStep, projectId, since: null, ct).ConfigureAwait(false)
            : await GetRunHistoryPagedInternalAsync(page, pageSize, finalStep, projectId, since: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxHistorySize);

        // When since is null and there are no other filters, fall through to the simpler feedback/plain paths
        // which have their own validation (including the page-offset overflow guard).
        if (since is null && finalStep is null && string.IsNullOrEmpty(projectId))
            return await GetRunHistoryAsync(page, pageSize, feedbackOnly, ct).ConfigureAwait(false);

        // TODO: the page-offset overflow guard ((long)(page-1)*pageSize > int.MaxValue) that lives in
        // GetRunHistoryAsync(page, pageSize, ct) is only reached via the fast-path above (since==null &&
        // no filters). When since is non-null the code falls through directly to the internal helpers
        // which apply Skip(offset) without the guard. In practice Insights always calls with page:1 so
        // this is not exploitable today, but the documented validation contract is silently broken for
        // this overload. Add the overflow guard here (or delegate to an overload that already has it)
        // if callers beyond Insights ever use this overload with large page numbers.
        return feedbackOnly
            ? await GetRunHistoryPagedWithFeedbackFilterInternalAsync(page, pageSize, finalStep, projectId, since, ct).ConfigureAwait(false)
            : await GetRunHistoryPagedInternalAsync(page, pageSize, finalStep, projectId, since, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, PipelineRunType? runType, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxHistorySize);

        // When no filters are set, fall through to the simpler unfiltered paths.
        if (since is null && finalStep is null && string.IsNullOrEmpty(projectId) && runType is null)
            return await GetRunHistoryAsync(page, pageSize, feedbackOnly, ct).ConfigureAwait(false);

        return feedbackOnly
            ? await GetRunHistoryPagedWithFeedbackFilterInternalAsync(page, pageSize, finalStep, projectId, since, runType, ct).ConfigureAwait(false)
            : await GetRunHistoryPagedInternalAsync(page, pageSize, finalStep, projectId, since, runType, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.RunId == runId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return entity is null ? null : DeserializeSummary(entity);
    }

    /// <inheritdoc />
    public void TryDeleteWorkspace(WorkspacePath? workspacePath, string runId, string workspaceBaseDirectory)
        => WorkspaceDeletionGuard.TryDelete(workspacePath?.Value, runId, workspaceBaseDirectory, _logger);

    // ── Async internals ─────────────────────────────────────────────────

    /// <summary>
    /// Shared paged-scan loop. Fetches batches using <paramref name="fetchBatch"/>, filters out
    /// legacy consolidation ghost rows (see <see cref="IsConsolidationGhost"/>), deserializes
    /// the remaining entries, applies <paramref name="include"/> as an additional predicate, and
    /// accumulates until <c>pageSize + 1</c> valid items are collected or the table is exhausted.
    /// Both paged history methods (plain and feedback-only) share this pattern — extracted in
    /// T22 (arch-audit 2026-08-22).
    /// </summary>
    private async Task<PagedResult<PipelineRunSummary>> ScanPagedAsync(
        PipelineDbContext db,
        int page,
        int pageSize,
        Func<PipelineDbContext, int, int, CancellationToken, Task<List<PipelineRunEntity>>> fetchBatch,
        Func<PipelineRunSummary, bool>? include,
        CancellationToken ct)
    {
        const int batchMultiplier = 2;
        var skip = checked((page - 1) * pageSize);
        var items = new List<PipelineRunSummary>();
        var dbOffset = skip;

        while (items.Count < pageSize + 1)
        {
            var batchSize = (pageSize + 1 - items.Count) * batchMultiplier;
            var entities = await fetchBatch(db, dbOffset, batchSize, ct).ConfigureAwait(false);

            if (entities.Count == 0)
                break;

            var batch = entities
                .Select(DeserializeSummary)
                .Where(s => s is not null && !IsConsolidationGhost(s!))
                .Where(s => include is null || include(s!))
                .Select(s => s!)
                .ToList();

            items.AddRange(batch);
            dbOffset += entities.Count;

            if (entities.Count < batchSize)
                break;
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();

        return new PagedResult<PipelineRunSummary>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    private async Task<PagedResult<PipelineRunSummary>> GetRunHistoryPagedWithFeedbackFilterInternalAsync(int page, int pageSize, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, CancellationToken ct)
        => await GetRunHistoryPagedWithFeedbackFilterInternalAsync(page, pageSize, finalStep, projectId, since, runType: null, ct).ConfigureAwait(false);

    private async Task<PagedResult<PipelineRunSummary>> GetRunHistoryPagedWithFeedbackFilterInternalAsync(int page, int pageSize, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, PipelineRunType? runType, CancellationToken ct)
    {
        // Filter feedbackOnly at the DB query level using Postgres JSONB `?` (key-exists) operator.
        // Since "Feedback" is embedded in the SummaryJson JSONB column (not a standalone column),
        // we use raw SQL to filter server-side before page offsets are computed.
        // This ensures the page boundary is correctly applied after the feedback filter —
        // unlike the export endpoint (faithful port of legacy in-memory-post-paging behaviour).
        // Spec 045 reconciles the divergence.
        // The optional outcome (FinalStep, int), project (ProjectId, text), since (StartedAt, timestamp),
        // and runType (RunType, int) filters are folded into the SAME SQL, so all filters apply before OFFSET/LIMIT.
        // Filter values flow through EF DbParameters ({n}), so projectId is safe.
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sql = @"SELECT * FROM ""PipelineRuns"" WHERE ""SummaryJson"" IS NOT NULL AND ""SummaryJson"" ? 'Feedback' AND ""SummaryJson"" ->> 'Feedback' IS NOT NULL";
        var filterArgs = new List<object>();
        if (finalStep is { } step) { sql += " AND \"FinalStep\" = {" + filterArgs.Count + "}"; filterArgs.Add((int)step); }
        if (!string.IsNullOrEmpty(projectId)) { sql += " AND \"ProjectId\" = {" + filterArgs.Count + "}"; filterArgs.Add(projectId); }
        if (since is { } sinceValue) { sql += " AND \"StartedAt\" >= {" + filterArgs.Count + "}"; filterArgs.Add(sinceValue); }
        if (runType is { } rt) { sql += " AND \"RunType\" = {" + filterArgs.Count + "}"; filterArgs.Add((int)rt); }
        // TODO: [WARNING] filterArgs is List<object>, so sinceValue (DateTimeOffset) is boxed and EF Core
        // must infer the DB type (timestamptz) from the CLR type at runtime. Npgsql currently maps
        // DateTimeOffset → timestamp with time zone correctly, matching the StartedAt column type.
        // If the column were remapped to timestamp without time zone this would fail at runtime with
        // a type mismatch rather than a compile-time error. Prefer List<NpgsqlParameter> with an
        // explicit NpgsqlDbType.TimestampTz if the raw SQL path is extended. (DotNetSpecialist review finding #3077.)
        // NOTE: filterArgs index fragility — OFFSET/LIMIT indices are derived from filterArgs.Count
        // immediately after the filter block. Any future addition of a filter here must continue
        // appending to filterArgs before this line and must NOT insert args after it.
        sql += " ORDER BY \"StartedAt\" DESC OFFSET {" + filterArgs.Count + "} LIMIT {" + (filterArgs.Count + 1) + "}";

        return await ScanPagedAsync(db, page, pageSize,
            fetchBatch: async (db, offset, batchSize, innerCt) =>
            {
                var args = new List<object>(filterArgs) { offset, batchSize };
                return await db.PipelineRuns
                    .FromSqlRaw(sql, args.ToArray())
                    .AsNoTracking().ToListAsync(innerCt).ConfigureAwait(false);
            },
            include: s => s.Feedback is not null,
            ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryInternalAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.PipelineRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxHistorySize)
            .ToListAsync(ct).ConfigureAwait(false);

        // The ghost-only filter excludes legacy consolidation ghost rows (IssueProviderConfigId =
        // consolidation sentinel AND SummaryJson is null or corrupt). Real consolidation runs
        // (post-#3024) have a valid SummaryJson with RunType = PipelineRunType.Consolidation and
        // are returned normally. This replaces the former InitiatedBy prefix filter which
        // incorrectly excluded real consolidation runs as well as ghosts.
        return entities
            .Select(DeserializeSummary)
            .Where(s => s is not null && !IsConsolidationGhost(s!))
            .Select(s => s!)
            .ToList();
    }

    private async Task<PagedResult<PipelineRunSummary>> GetRunHistoryPagedInternalAsync(int page, int pageSize, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, CancellationToken ct)
        => await GetRunHistoryPagedInternalAsync(page, pageSize, finalStep, projectId, since, runType: null, ct).ConfigureAwait(false);

    private async Task<PagedResult<PipelineRunSummary>> GetRunHistoryPagedInternalAsync(int page, int pageSize, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, PipelineRunType? runType, CancellationToken ct)
    {
        // We need pageSize + 1 valid (non-consolidation-ghost) items to determine HasMore.
        // Because consolidation ghost entries may exist in the table (defense-in-depth filter),
        // we over-fetch and loop until we have enough valid items or exhaust the table.
        // When runType=Consolidation is set, ghost rows are excluded by the DB filter itself
        // (ghosts have RunType != Consolidation), so over-fetching is minimised.
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await ScanPagedAsync(db, page, pageSize,
            fetchBatch: async (db, offset, batchSize, innerCt) =>
            {
                // Outcome (FinalStep), project (ProjectId), since (StartedAt), and runType (RunType)
                // filters run DB-side, before the page offset — so paging is correct across history.
                IQueryable<PipelineRunEntity> q = db.PipelineRuns.AsNoTracking();
                if (finalStep is { } step)
                    q = q.Where(r => r.FinalStep == step);
                if (!string.IsNullOrEmpty(projectId))
                    q = q.Where(r => r.ProjectId == projectId);
                if (since is { } sinceValue)
                    q = q.Where(r => r.StartedAt >= sinceValue);
                if (runType is { } rt)
                    q = q.Where(r => r.RunType == rt);
                return await q
                    .OrderByDescending(r => r.StartedAt)
                    .Skip(offset)
                    .Take(batchSize)
                    .ToListAsync(innerCt)
                    .ConfigureAwait(false);
            },
            include: null,
            ct).ConfigureAwait(false);
    }

    private async Task AddRunToHistoryInternalAsync(PipelineRunSummary summary, CancellationToken ct)
    {
        var entity = ToEntity(summary);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Upsert: a PipelineRunEntity row may already exist (created at dispatch time
        // by DispatchOrchestrationService for active run tracking). Update it with final state.
        var existing = await db.PipelineRuns.FindAsync([entity.RunId], ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.IssueIdentifier = entity.IssueIdentifier;
            existing.IssueTitle = entity.IssueTitle;
            existing.FinalStep = entity.FinalStep;
            existing.CompletedAt = entity.CompletedAt;
            existing.RetryCount = entity.RetryCount;
            existing.PullRequestUrl = entity.PullRequestUrl;
            existing.ModelName = entity.ModelName;
            existing.AgentId = entity.AgentId;
            existing.ProjectId = entity.ProjectId;
            existing.ProjectName = entity.ProjectName;
            existing.RunType = entity.RunType;
            existing.IssueProviderConfigId = entity.IssueProviderConfigId;
            existing.SummaryJson = entity.SummaryJson;
            existing.HarnessVersion = entity.HarnessVersion;
            existing.WorkItemId = entity.WorkItemId;
        }
        else
        {
            db.PipelineRuns.Add(entity);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsPrimaryKeyViolation(ex))
        {
            // Concurrent insert race: another thread inserted the same RunId between
            // FindAsync (miss) and SaveChangesAsync. Retry as update.
            _logger.Warning(ex, "Upsert race for run {RunId}, retrying as update", entity.RunId);
            db.ChangeTracker.Clear();
            var retry = await db.PipelineRuns.FindAsync([entity.RunId], ct).ConfigureAwait(false);
            if (retry is not null)
            {
                retry.IssueIdentifier = entity.IssueIdentifier;
                retry.IssueTitle = entity.IssueTitle;
                retry.FinalStep = entity.FinalStep;
                retry.CompletedAt = entity.CompletedAt;
                retry.RetryCount = entity.RetryCount;
                retry.PullRequestUrl = entity.PullRequestUrl;
                retry.ModelName = entity.ModelName;
                retry.AgentId = entity.AgentId;
                retry.ProjectId = entity.ProjectId;
                retry.ProjectName = entity.ProjectName;
                retry.RunType = entity.RunType;
                retry.IssueProviderConfigId = entity.IssueProviderConfigId;
                retry.SummaryJson = entity.SummaryJson;
                retry.HarnessVersion = entity.HarnessVersion;
                retry.WorkItemId = entity.WorkItemId;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    // ── Mapping ─────────────────────────────────────────────────────────

    private static PipelineRunEntity ToEntity(PipelineRunSummary summary)
    {
        return new PipelineRunEntity
        {
            RunId = Guid.TryParse(summary.RunId, out var id) ? id : Guid.NewGuid(),
            IssueIdentifier = summary.IssueIdentifier,
            IssueTitle = summary.IssueTitle,
            FinalStep = summary.FinalStep,
            StartedAt = summary.StartedAtOffset != default
                ? summary.StartedAtOffset
                : new DateTimeOffset(summary.StartedAt, TimeSpan.Zero),
            CompletedAt = summary.CompletedAtOffset
                ?? (summary.CompletedAt.HasValue
                    ? new DateTimeOffset(summary.CompletedAt.Value, TimeSpan.Zero)
                    : null),
            RetryCount = summary.RetryCount,
            PullRequestUrl = summary.PullRequestUrl,
            ModelName = summary.ModelName,
            AgentId = summary.AgentId,
            ProjectId = summary.ProjectId,
            ProjectName = summary.ProjectName,
            RunType = summary.RunType,
            // Derive IssueProviderConfigId from InitiatedBy: consolidation runs carry the sentinel,
            // all other runs leave it null. Used by DeserializeSummary to reconstruct InitiatedBy
            // when SummaryJson is null or corrupt. Note: this mapping relies on InitiatedBy being
            // set correctly before AddRunToHistoryAsync is called; there is no validation at the
            // API boundary.
            IssueProviderConfigId = summary.InitiatedBy.StartsWith(ConsolidationConstants.InitiatedByPrefix, StringComparison.Ordinal)
                ? ConsolidationConstants.ProviderConfigId
                : null,
            SummaryJson = JsonSerializer.Serialize(summary, JsonOptions),
            HarnessVersion = summary.HarnessVersion,
            // Propagate WorkItemId to the first-class column (additive/nullable — safe for old rows).
            // For new runs RunId == WorkItemId by contract; for backfilled consolidation rows the
            // WorkItemId carried through from the ConsolidationRun.WorkItemId field.
            WorkItemId = summary.WorkItemId
        };
    }

    private PipelineRunSummary? DeserializeSummary(PipelineRunEntity entity)
    {
        // Prefer full JSON round-trip if available
        if (!string.IsNullOrEmpty(entity.SummaryJson))
        {
            try
            {
                return JsonSerializer.Deserialize<PipelineRunSummary>(entity.SummaryJson, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.Warning(ex, "Failed to deserialize SummaryJson for run {RunId}, falling back to columns",
                    entity.RunId);
            }
        }

        // Fallback: reconstruct from columns (for rows inserted before SummaryJson was added,
        // or when SummaryJson is corrupt). IssueProviderConfigId is set to
        // ConsolidationConstants.ProviderConfigId for consolidation runs (see ToEntity), so we
        // can reliably reconstruct InitiatedBy even without SummaryJson.
        return new PipelineRunSummary
        {
            RunId = entity.RunId.ToString(),
            IssueIdentifier = entity.IssueIdentifier,
            IssueTitle = entity.IssueTitle ?? "",
            FinalStep = entity.FinalStep,
            StartedAtOffset = entity.StartedAt,
            CompletedAtOffset = entity.CompletedAt,
            RetryCount = entity.RetryCount,
            PullRequestUrl = entity.PullRequestUrl,
            ModelName = entity.ModelName,
            AgentId = entity.AgentId,
            // Reconstruct InitiatedBy from IssueProviderConfigId column:
            // - consolidation sentinel → "consolidation:manual" (excluded by read-time filter)
            // - null (legacy rows or normal runs) → "manual" (default, passes read-time filter)
            // Note: hard-coding "manual" for non-consolidation rows is a lossy approximation.
            // Any run with a different original InitiatedBy value (e.g. "loop:issue") that loses
            // its SummaryJson will surface as InitiatedBy="manual". For filtering purposes this is
            // correct (non-consolidation rows must not be excluded), but the fallback path cannot
            // reconstruct the original value without a dedicated column.
            InitiatedBy = entity.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId
                ? ConsolidationConstants.InitiatedBy
                : InitiatedByConstants.Manual,
            // Note: ProjectId is not recovered in this fallback path — it is lost when SummaryJson
            // is null or corrupt. A dedicated column would be needed to preserve it for legacy rows.
            ProjectName = entity.ProjectName,
            RunType = entity.RunType
        };
    }

    /// <summary>
    /// Returns true for legacy consolidation ghost rows that must remain hidden from history.
    /// A ghost is a row whose <see cref="PipelineRunSummary.InitiatedBy"/> starts with the
    /// consolidation prefix (either from the SummaryJson or reconstructed by the fallback path)
    /// but whose <see cref="PipelineRunSummary.RunType"/> is not
    /// <see cref="PipelineRunType.Consolidation"/>. This catches:
    /// <list type="bullet">
    ///   <item>Null-SummaryJson ghosts — fallback reconstructs InitiatedBy from the sentinel IssueProviderConfigId,
    ///     RunType defaults to Implementation.</item>
    ///   <item>Corrupt-SummaryJson ghosts — JSON deserialization fails, fallback runs, same result.</item>
    /// </list>
    /// Real consolidation runs (post-#3024) have a valid SummaryJson that deserializes with
    /// <see cref="PipelineRunType.Consolidation"/>, so they pass this filter and are returned.
    /// </summary>
    private static bool IsConsolidationGhost(PipelineRunSummary summary)
        => summary.InitiatedBy?.StartsWith(ConsolidationConstants.InitiatedByPrefix, StringComparison.Ordinal) == true
           && summary.RunType != PipelineRunType.Consolidation;

    /// <summary>
    /// Detects PK violation exceptions from Npgsql (SQLSTATE 23505) or generic message-based
    /// fallbacks. Delegates to <see cref="PostgresErrorClassifier.IsUniqueViolation"/>.
    /// </summary>
    private static bool IsPrimaryKeyViolation(DbUpdateException ex)
        => PostgresErrorClassifier.IsUniqueViolation(ex);
}
