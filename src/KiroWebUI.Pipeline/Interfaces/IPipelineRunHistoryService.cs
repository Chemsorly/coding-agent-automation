using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

/// <summary>
/// Manages pipeline run history: persistence, retrieval, and workspace cleanup.
/// </summary>
public interface IPipelineRunHistoryService
{
    IReadOnlyList<PipelineRunSummary> GetRunHistory();
    void AddRunToHistory(PipelineRun run);
    void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory);
    void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null);
}
