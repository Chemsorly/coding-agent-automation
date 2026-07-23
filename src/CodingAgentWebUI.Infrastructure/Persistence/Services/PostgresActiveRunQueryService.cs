using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// DB-backed implementation of <see cref="IActiveRunQueryService"/>.
/// Queries WorkItems WHERE Status IN (Dispatched, Running) joined with PipelineRuns
/// for display fields. Enriches results with live in-memory state from
/// <see cref="IOrchestratorRunService"/> for real-time step/agent updates.
/// </summary>
public sealed class PostgresActiveRunQueryService : IActiveRunQueryService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IOrchestratorRunService? _runService;

    public PostgresActiveRunQueryService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService? runService = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _runService = runService;
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
            where (wi.Status == WorkItemStatus.Dispatched || wi.Status == WorkItemStatus.Running)
                && wi.TaskType != WorkItemTaskType.Consolidation
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

        // Enrich with live in-memory state (real-time step transitions, agent assignment)
        if (_runService is not null)
        {
            for (var i = 0; i < summaries.Count; i++)
            {
                var liveRun = _runService.GetRun(summaries[i].RunId);
                if (liveRun is null) continue;

                summaries[i] = summaries[i] with
                {
                    CurrentStep = liveRun.CurrentStep,
                    AgentId = summaries[i].AgentId ?? liveRun.AgentId,
                    IssueTitle = !string.IsNullOrEmpty(liveRun.IssueTitle) ? liveRun.IssueTitle : summaries[i].IssueTitle,
                    ProjectName = summaries[i].ProjectName ?? liveRun.ProjectName
                };
            }

            // Append in-memory-only runs that have no matching WorkItem in the DB.
            // This covers runs restored via agent reconnection (RegisterAgent) where
            // no WorkItem row exists — without this, monitoring shows fewer active runs
            // than busy agents.
            var dbRunIds = new HashSet<string>(summaries.Select(s => s.RunId), StringComparer.OrdinalIgnoreCase);
            foreach (var liveRun in _runService.GetActiveRuns())
            {
                if (dbRunIds.Contains(liveRun.RunId))
                    continue;

                // Skip consolidation ghost runs (should not exist with proper filtering,
                // but defensive against edge cases)
                if (liveRun.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
                    continue;

                summaries.Add(new ActiveRunSummary
                {
                    RunId = liveRun.RunId,
                    IssueIdentifier = liveRun.IssueIdentifier,
                    IssueTitle = liveRun.IssueTitle ?? "",
                    RunType = liveRun.RunType,
                    AgentId = liveRun.AgentId,
                    StartedAt = liveRun.StartedAtOffset,
                    ProjectName = liveRun.ProjectName,
                    CurrentStep = liveRun.CurrentStep
                });
            }
        }

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
