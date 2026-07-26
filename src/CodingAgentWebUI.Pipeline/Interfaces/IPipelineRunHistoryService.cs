using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Manages pipeline run history: persistence, retrieval, and workspace cleanup.
/// </summary>
public interface IPipelineRunHistoryService
{
    void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory);
    void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null);

    /// <summary>Persists a completed run to history.</summary>
    Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default);

    /// <summary>Retrieves the run history.</summary>
    Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default);

    /// <summary>Retrieves the run history with pagination.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default);
}
