using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// In-memory pipeline run history service. No file I/O.
/// </summary>
public sealed class InMemoryPipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly List<PipelineRunSummary> _history = new();

    public void Reset() => _history.Clear();

    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PipelineRunSummary>>(_history.ToList().AsReadOnly());

    // TODO: This fake does not filter by InitiatedBy != ConsolidationConstants.InitiatedBy, unlike the real
    // PostgresPipelineRunHistoryService. If E2E tests rely on this for pagination validation, results may
    // diverge from production behavior. Consider adding a contract test or aligning the filter logic.
    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var items = _history.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        });
    }

    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
    {
        _history.Insert(0, run.ToSummary());
        return Task.CompletedTask;
    }

    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
}
