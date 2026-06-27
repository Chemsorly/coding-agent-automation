using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// DB-backed implementation of <see cref="IActiveRunQueryService"/>.
/// Queries WorkItems WHERE Status IN (Dispatched, Running) joined with PipelineRuns
/// for display fields. Uses <c>AsNoTracking()</c> for read-only performance.
/// Enables non-leader replicas to display current run state from Postgres
/// without needing in-memory sync from the leader.
/// </summary>
public sealed class PostgresActiveRunQueryService : IActiveRunQueryService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public PostgresActiveRunQueryService(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    public async Task<IReadOnlyList<ActiveRunSummary>> GetActiveRunsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Query active work items joined with their PipelineRun records for display fields.
        // Left join: work items might not yet have a PipelineRun row (just dispatched).
        // Project to anonymous type server-side, then map to ActiveRunSummary client-side
        // to avoid EF Core translation issues with helper methods.
        var rows = await (
            from wi in db.WorkItems.AsNoTracking()
            where wi.Status == WorkItemStatus.Dispatched || wi.Status == WorkItemStatus.Running
            join pr in db.PipelineRuns.AsNoTracking()
                on wi.Id equals pr.WorkItemId into runs
            from pr in runs.DefaultIfEmpty()
            select new
            {
                WorkItemId = wi.Id,
                wi.IssueIdentifier,
                wi.Status,
                wi.TaskType,
                wi.AssignedAgentId,
                wi.DispatchedAt,
                wi.CreatedAt,
                RunId = pr != null ? pr.RunId : (Guid?)null,
                IssueTitle = pr != null ? pr.IssueTitle : null,
                RunType = pr != null ? pr.RunType : (PipelineRunType?)null,
                AgentId = pr != null ? pr.AgentId : null,
                ProjectName = pr != null ? pr.ProjectName : null
            }
        ).ToListAsync(ct);

        var summaries = rows.Select(r => new ActiveRunSummary
        {
            RunId = r.RunId?.ToString() ?? r.WorkItemId.ToString(),
            IssueIdentifier = r.IssueIdentifier,
            IssueTitle = r.IssueTitle ?? "",
            RunType = r.RunType ?? MapTaskTypeToRunType(r.TaskType),
            AgentId = r.AssignedAgentId ?? r.AgentId,
            StartedAt = r.DispatchedAt ?? r.CreatedAt,
            ProjectName = r.ProjectName,
            CurrentStep = MapStatusToStep(r.Status)
        }).ToList();

        return summaries;
    }

    /// <summary>
    /// Maps WorkItemStatus to a representative PipelineStep for UI display.
    /// </summary>
    private static PipelineStep MapStatusToStep(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Dispatched => PipelineStep.Created,
        WorkItemStatus.Running => PipelineStep.GeneratingCode,
        _ => PipelineStep.Created
    };

    /// <summary>
    /// Maps WorkItemTaskType to PipelineRunType when no PipelineRun join exists.
    /// </summary>
    private static PipelineRunType MapTaskTypeToRunType(WorkItemTaskType taskType) => taskType switch
    {
        WorkItemTaskType.Implementation => PipelineRunType.Implementation,
        WorkItemTaskType.Review => PipelineRunType.Review,
        WorkItemTaskType.Decomposition => PipelineRunType.Decomposition,
        WorkItemTaskType.Consolidation => PipelineRunType.Implementation, // best-effort fallback
        _ => PipelineRunType.Implementation
    };
}
