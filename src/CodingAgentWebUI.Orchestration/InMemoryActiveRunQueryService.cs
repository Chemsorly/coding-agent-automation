using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Legacy/single-instance implementation of <see cref="IActiveRunQueryService"/>.
/// Delegates to <see cref="OrchestratorRunService"/> in-memory state for active run display.
/// Used when no database is configured (docker-compose without Postgres).
/// </summary>
public sealed class InMemoryActiveRunQueryService : IActiveRunQueryService
{
    private readonly IOrchestratorRunService _runService;

    public InMemoryActiveRunQueryService(IOrchestratorRunService runService)
    {
        _runService = runService ?? throw new ArgumentNullException(nameof(runService));
    }

    public Task<IReadOnlyList<ActiveRunSummary>> GetActiveRunsAsync(CancellationToken ct = default)
    {
        var runs = _runService.GetActiveRuns();
        var summaries = runs.Select(r => new ActiveRunSummary
        {
            RunId = r.RunId,
            IssueIdentifier = r.IssueIdentifier.Value,
            IssueTitle = r.IssueTitle,
            RunType = r.RunType,
            AgentId = r.AgentId,
            StartedAt = r.StartedAtOffset,
            ProjectName = r.ProjectName,
            CurrentStep = r.CurrentStep
        }).ToList();

        return Task.FromResult<IReadOnlyList<ActiveRunSummary>>(summaries);
    }
}
