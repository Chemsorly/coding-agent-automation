using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

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

        // Defense-in-depth: reject consolidation runs from being persisted to pipeline history.
        // Consolidation has its own history on the Consolidation page.
        if (run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
        {
            _logger.Debug("AddRunToHistoryAsync: skipping consolidation run {RunId}", run.RunId);
            return;
        }

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
        // TODO: Add upper bound for 'page' parameter to prevent integer overflow in (page - 1) * pageSize.
        // With page=2_147_485 and pageSize=1000, unchecked multiplication wraps negative.

        return await GetRunHistoryPagedInternalAsync(page, pageSize, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory)
    {
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return;

        var dirInfo = new DirectoryInfo(workspacePath);
        if (dirInfo.LinkTarget != null)
        {
            _logger.Warning("Pipeline {RunId} workspace {Path} is a symlink, skipping cleanup",
                runId, workspacePath);
            return;
        }

        var fullPath = Path.GetFullPath(workspacePath);
        var fullBase = Path.GetFullPath(workspaceBaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullBase, StringComparison.Ordinal) ||
            fullPath.TrimEnd(Path.DirectorySeparatorChar) == fullBase.TrimEnd(Path.DirectorySeparatorChar))
        {
            _logger.Warning("Pipeline {RunId} workspace path {Path} is not inside base {Base}, skipping cleanup",
                runId, workspacePath, workspaceBaseDirectory);
            return;
        }

        try
        {
            Directory.Delete(workspacePath, recursive: true);
            _logger.Information("Pipeline {RunId} workspace deleted: {Path}", runId, workspacePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to delete workspace: {Path}", runId, workspacePath);
        }
    }

    /// <inheritdoc />
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.FailedWorkspaceRetentionDays < 0)
            return;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-config.FailedWorkspaceRetentionDays);

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var query = db.PipelineRuns
                .AsNoTracking()
                .Where(r => r.FinalStep != PipelineStep.Completed)
                .Where(r => r.CompletedAt != null && r.CompletedAt < cutoff);

            if (!string.IsNullOrEmpty(activeRunId) && Guid.TryParse(activeRunId, out var activeGuid))
                query = query.Where(r => r.RunId != activeGuid);

            var expiredRuns = query
                .Select(r => new { RunId = r.RunId.ToString(), CompletedAt = r.CompletedAt!.Value })
                .ToList();

            foreach (var expired in expiredRuns)
            {
                var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, expired.RunId);
                TryDeleteWorkspace(workspacePath, expired.RunId, config.WorkspaceBaseDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to query expired runs for workspace cleanup");
        }
    }

    // ── Async internals ─────────────────────────────────────────────────

    private async Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryInternalAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.PipelineRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxHistorySize)
            .ToListAsync(ct).ConfigureAwait(false);

        // The read-time filter uses InitiatedBy (from SummaryJson). DeserializeSummary sets InitiatedBy
        // from entity.IssueProviderConfigId in the column-fallback path, so this filter is correct
        // even when SummaryJson is null or corrupt — consolidation ghost entries are excluded in both paths.
        return entities
            .Select(DeserializeSummary)
            .Where(s => s is not null && s.InitiatedBy != ConsolidationConstants.InitiatedBy)
            .Select(s => s!)
            .ToList();
    }

    private async Task<PagedResult<PipelineRunSummary>> GetRunHistoryPagedInternalAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // We need pageSize + 1 valid (non-consolidation) items to determine HasMore.
        // Because consolidation ghost entries may exist in the table (defense-in-depth filter),
        // we over-fetch and loop until we have enough valid items or exhaust the table.
        var skip = checked((page - 1) * pageSize);
        const int batchMultiplier = 2; // Over-fetch factor to reduce round-trips
        var items = new List<PipelineRunSummary>();
        var dbOffset = skip;
        var hasMore = false;

        while (items.Count < pageSize + 1)
        {
            var batchSize = (pageSize + 1 - items.Count) * batchMultiplier;
            var entities = await db.PipelineRuns
                .AsNoTracking()
                .OrderByDescending(r => r.StartedAt)
                .Skip(dbOffset)
                .Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);

            if (entities.Count == 0)
                break; // No more rows in the table

            var batch = entities
                .Select(DeserializeSummary)
                .Where(s => s is not null && s.InitiatedBy != ConsolidationConstants.InitiatedBy)
                .Select(s => s!)
                .ToList();

            items.AddRange(batch);
            dbOffset += entities.Count;

            // If we fetched fewer rows than requested, we've exhausted the table
            if (entities.Count < batchSize)
                break;
        }

        hasMore = items.Count > pageSize;
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
            // when SummaryJson is null or corrupt.
            // TODO: This mapping relies entirely on InitiatedBy being set correctly before AddRunToHistoryAsync
            // is called. There is no validation at the API boundary. A PipelineRunSummary with
            // InitiatedBy = "consolidation" that is NOT a consolidation run would be stored with the
            // consolidation sentinel in IssueProviderConfigId and subsequently excluded from history.
            IssueProviderConfigId = summary.InitiatedBy == ConsolidationConstants.InitiatedBy
                ? ConsolidationConstants.ProviderConfigId
                : null,
            SummaryJson = JsonSerializer.Serialize(summary, JsonOptions)
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
            // - consolidation sentinel → "consolidation" (excluded by read-time filter)
            // - null (legacy rows or normal runs) → "manual" (default, passes read-time filter)
            // TODO: Hard-coding "manual" for non-consolidation rows is a lossy approximation.
            // Any run with a different original InitiatedBy value (e.g. "loop", a username) that
            // loses its SummaryJson will silently surface as InitiatedBy="manual" via this path.
            // This is a known semantic gap: the fallback path cannot reconstruct the original
            // InitiatedBy without storing it in a dedicated column. For filtering purposes this
            // is correct (non-consolidation rows must not be excluded), but callers that display
            // or aggregate InitiatedBy should be aware of this lossy reconstruction.
            // See: DotNetSpecialist review warning, issue #1918.
            InitiatedBy = entity.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId
                ? ConsolidationConstants.InitiatedBy
                : "manual",
            // TODO: Add ProjectId = entity.ProjectId here for consistency — fallback path loses ProjectId when SummaryJson is null/corrupt
            ProjectName = entity.ProjectName,
            RunType = entity.RunType
        };
    }

    /// <summary>
    /// Detects PK violation exceptions from Npgsql (code 23505) or generic DbUpdateException
    /// wrapping a unique constraint violation.
    /// </summary>
    private static bool IsPrimaryKeyViolation(DbUpdateException ex)
    {
        // Npgsql wraps PostgreSQL error 23505 (unique_violation) in a PostgresException.
        // For in-memory provider (tests), there's no inner Npgsql exception — treat any
        // DbUpdateException during Add as a potential duplicate.
        var inner = ex.InnerException;
        if (inner is not null && inner.GetType().Name == "PostgresException")
        {
            // Npgsql PostgresException has a SqlState property
            var sqlStateProp = inner.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(inner) is string sqlState)
                return sqlState == "23505";
        }

        // Fallback: treat as PK violation if it's a generic constraint error
        return ex.InnerException?.Message?.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message?.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true;
    }
}
