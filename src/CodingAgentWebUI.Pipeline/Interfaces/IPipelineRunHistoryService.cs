using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Manages pipeline run history: persistence, retrieval, and workspace cleanup.
/// </summary>
public interface IPipelineRunHistoryService
{
    IReadOnlyList<PipelineRunSummary> GetRunHistory();
    IReadOnlyList<PipelineRunSummary> GetRunsByAgentId(string agentId, int limit = 10);
    void AddRunToHistory(PipelineRun run);
    void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory);
    void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null);

    /// <summary>Async overload of <see cref="AddRunToHistory"/>. Default delegates to sync version.</summary>
    Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
        => Task.Run(() => AddRunToHistory(run), ct);

    /// <summary>Async overload of <see cref="GetRunHistory"/>. Default delegates to sync version.</summary>
    Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => Task.FromResult(GetRunHistory());
}
