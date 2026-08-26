using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// No-op implementation of <see cref="IPipelineRunHistoryService"/> for the agent context.
/// The agent does not maintain run history — it only executes pipeline steps.
/// Registering this eliminates null entirely at the DI level.
/// </summary>
public sealed class NullPipelineRunHistoryService : IPipelineRunHistoryService
{
    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PipelineRunSummary>>([]);

    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = [],
            Page = page,
            PageSize = pageSize,
            HasMore = false
        });

    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = [],
            Page = page,
            PageSize = pageSize,
            HasMore = false
        });

    public Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default)
        => Task.FromResult<PipelineRunSummary?>(null);

    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default)
        => Task.CompletedTask;

    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }

    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
}
