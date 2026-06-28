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
/// Indexed columns (AgentId, StartedAt, FinalStep) enable efficient queries.
/// </summary>
public sealed class PostgresPipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILogger _logger;

    /// <summary>Maximum number of run summaries returned by <see cref="GetRunHistory"/>.</summary>
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
    public IReadOnlyList<PipelineRunSummary> GetRunHistory()
    {
        // Synchronous wrapper over async DB call — acceptable here because callers
        // are already in synchronous context (HeartbeatMonitor, ConsolidationService).
        // Uses GetAwaiter().GetResult() to avoid deadlocks in sync-over-async.
        return GetRunHistoryAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public IReadOnlyList<PipelineRunSummary> GetRunsByAgentId(string agentId, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return GetRunsByAgentIdAsync(agentId, limit).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void AddRunToHistory(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var summary = run.ToSummary();

        try
        {
            AddRunToHistoryAsync(summary).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist run summary {RunId} to database", summary.RunId);
        }
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
            var expiredRuns = GetExpiredFailedRunsAsync(cutoff, activeRunId).GetAwaiter().GetResult();

            foreach (var (runId, completedAt) in expiredRuns)
            {
                var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, runId);
                TryDeleteWorkspace(workspacePath, runId, config.WorkspaceBaseDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to query expired runs for workspace cleanup");
        }
    }

    // ── Async internals ─────────────────────────────────────────────────

    private async Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.PipelineRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxHistorySize)
            .ToListAsync();

        return entities.Select(DeserializeSummary).Where(s => s is not null).ToList()!;
    }

    private async Task<IReadOnlyList<PipelineRunSummary>> GetRunsByAgentIdAsync(string agentId, int limit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.AgentId == agentId)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToListAsync();

        return entities.Select(DeserializeSummary).Where(s => s is not null).ToList()!;
    }

    private async Task AddRunToHistoryAsync(PipelineRunSummary summary)
    {
        var entity = ToEntity(summary);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Upsert: a PipelineRunEntity row may already exist (created at dispatch time
        // by DispatchOrchestrationService for active run tracking). Update it with final state.
        var existing = await db.PipelineRuns.FindAsync(entity.RunId);
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

        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<(string RunId, DateTimeOffset CompletedAt)>> GetExpiredFailedRunsAsync(
        DateTimeOffset cutoff, string? activeRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.FinalStep != PipelineStep.Completed)
            .Where(r => r.CompletedAt != null && r.CompletedAt < cutoff);

        if (!string.IsNullOrEmpty(activeRunId) && Guid.TryParse(activeRunId, out var activeGuid))
            query = query.Where(r => r.RunId != activeGuid);

        var results = await query
            .Select(r => new { RunId = r.RunId.ToString(), CompletedAt = r.CompletedAt!.Value })
            .ToListAsync();

        return results.Select(r => (r.RunId, r.CompletedAt)).ToList();
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
}
