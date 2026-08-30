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
        var rows = await db.WorkItems
            .AsNoTracking()
            .WhereActive()
            .Where(wi => wi.TaskType != WorkItemTaskType.Consolidation)
            .GroupJoin(
                db.PipelineRuns.AsNoTracking(),
                wi => wi.Id,
                pr => pr.WorkItemId,
                (wi, runs) => new { wi, runs })
            .SelectMany(
                x => x.runs.DefaultIfEmpty(),
                (x, pr) => new
                {
                    WorkItemId = x.wi.Id,
                    x.wi.IssueIdentifier,
                    x.wi.Status,
                    x.wi.TaskType,
                    x.wi.AssignedAgentId,
                    x.wi.DispatchedAt,
                    x.wi.CreatedAt,
                    RunId = pr != null ? pr.RunId : (Guid?)null,
                    IssueTitle = pr != null ? pr.IssueTitle : null,
                    RunType = pr != null ? pr.RunType : (PipelineRunType?)null,
                    AgentId = pr != null ? pr.AgentId : null,
                    ProjectName = pr != null ? pr.ProjectName : null
                })
            .ToListAsync(ct);

        var summaries = rows.Select(r => MapRowToSummary(new ActiveRunRow(
            r.RunId, r.WorkItemId, r.IssueIdentifier,
            r.IssueTitle, r.RunType, r.TaskType, r.AssignedAgentId, r.AgentId,
            r.DispatchedAt, r.CreatedAt, r.ProjectName, r.Status))).ToList();

        EnrichWithLiveState(summaries);

        return summaries;
    }

    private static ActiveRunSummary MapRowToSummary(ActiveRunRow r)
    {
        var agentIdStr = r.AssignedAgentId ?? r.AgentId;
        return new ActiveRunSummary
        {
            RunId = r.RunId?.ToString() ?? r.WorkItemId.ToString(),
            IssueIdentifier = r.IssueIdentifier,
            IssueTitle = r.IssueTitle ?? "",
            // TODO: The query pre-filters out WorkItemTaskType.Consolidation rows (via a .Where clause
            // upstream), so Consolidation items never reach this mapper during active-run display.
            // ToDefaultRunType() handles Consolidation correctly and harmlessly, but if that pre-filter
            // is ever removed, Consolidation items would start appearing in the active-run list.
            // Keep the pre-filter and this comment in sync when modifying the query. (review: #2159)
            RunType = r.RunType ?? r.TaskType.ToDefaultRunType(),
            AgentId = !string.IsNullOrEmpty(agentIdStr) ? (AgentId)agentIdStr : (AgentId?)null,
            StartedAt = r.DispatchedAt ?? r.CreatedAt,
            ProjectName = r.ProjectName,
            CurrentStep = MapStatusToStep(r.Status)
        };
    }

    private void EnrichWithLiveState(List<ActiveRunSummary> summaries)
    {
        if (_runService is null) return;

        for (var i = 0; i < summaries.Count; i++)
        {
            var liveRun = _runService.GetRun(summaries[i].RunId);
            if (liveRun is null) continue;

            summaries[i] = summaries[i] with
            {
                CurrentStep = liveRun.CurrentStep,
                AgentId = summaries[i].AgentId ?? (!string.IsNullOrEmpty(liveRun.AgentId) ? (AgentId)liveRun.AgentId : (AgentId?)null),
                IssueTitle = !string.IsNullOrEmpty(liveRun.IssueTitle) ? liveRun.IssueTitle : summaries[i].IssueTitle,
                ProjectName = summaries[i].ProjectName ?? liveRun.ProjectName
            };
        }

        var dbRunIds = new HashSet<string>(summaries.Select(s => s.RunId), StringComparer.OrdinalIgnoreCase);
        AppendInMemoryOnlyRuns(summaries, dbRunIds);
    }

    private void AppendInMemoryOnlyRuns(List<ActiveRunSummary> summaries, HashSet<string> dbRunIds)
    {
        // Append in-memory-only runs that have no matching WorkItem in the DB.
        // This covers runs restored via agent reconnection (RegisterAgent) where
        // no WorkItem row exists — without this, monitoring shows fewer active runs
        // than busy agents.
        foreach (var liveRun in _runService!.GetActiveRuns())
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
                AgentId = !string.IsNullOrEmpty(liveRun.AgentId) ? (AgentId)liveRun.AgentId : (AgentId?)null,
                StartedAt = liveRun.StartedAtOffset,
                ProjectName = liveRun.ProjectName,
                CurrentStep = liveRun.CurrentStep
            });
        }
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

    private sealed record ActiveRunRow(
        Guid? RunId, Guid WorkItemId, string IssueIdentifier,
        string? IssueTitle, PipelineRunType? RunType, WorkItemTaskType TaskType,
        string? AssignedAgentId, string? AgentId,
        DateTimeOffset? DispatchedAt, DateTimeOffset CreatedAt,
        string? ProjectName, WorkItemStatus Status);
}