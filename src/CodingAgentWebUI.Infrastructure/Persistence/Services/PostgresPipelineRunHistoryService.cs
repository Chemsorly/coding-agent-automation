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
        // TODO: [BUG-12] This mutates run.CurrentStep on the caller's PipelineRun reference as a side effect.
        // Document this mutation contract on IPipelineRunHistoryService.AddRunToHistoryAsync, or clone before mutating,
        // so direct callers are aware their object may be modified.
        if (!run.CurrentStep.IsTerminal())
        {
            _logger.Warning(
                "AddRunToHistoryAsync: run {RunId} has non-terminal CurrentStep={Step}, forcing to Failed",
                run.RunId, run.CurrentStep);
            run.CurrentStep = PipelineStep.Failed;
        }

        var summary = run.ToSummary();

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

        // TODO: Write guard uses IssueProviderConfigId to reject consolidation runs, but read-time
        // filter uses InitiatedBy. If a consolidation run has the correct ProviderConfigId but
        // missing/null InitiatedBy (e.g., code path that sets one without the other), it could
        // leak through. Consider using the same discriminator for both write and read guards,
        // or adding a test verifying their interaction under failure conditions.
        return entities
            .Select(DeserializeSummary)
            .Where(s => s is not null && s.InitiatedBy != ConsolidationConstants.InitiatedBy)
            .ToList()!;
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
            existing.ProjectName = entity.ProjectName;
            existing.RunType = entity.RunType;
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
            _logger.Warning("Upsert race for run {RunId}, retrying as update", entity.RunId);
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
                retry.ProjectName = entity.ProjectName;
                retry.RunType = entity.RunType;
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
            ProjectName = summary.ProjectName,
            RunType = summary.RunType,
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

        // Fallback: reconstruct from columns (for rows inserted before SummaryJson was added)
        // TODO: InitiatedBy is not populated in this fallback path. If a consolidation ghost entry has
        // corrupt/null SummaryJson, it will produce a summary with InitiatedBy=null that passes through
        // the consolidation read-time filter (InitiatedBy != "consolidation"). Consider also checking
        // IssueProviderConfigId or adding an InitiatedBy column to the entity for robust filtering.
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
