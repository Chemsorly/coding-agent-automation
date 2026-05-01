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

    public IReadOnlyList<PipelineRunSummary> GetRunHistory() => _history.ToList().AsReadOnly();

    public IReadOnlyList<PipelineRunSummary> GetRunsByAgentId(string agentId, int limit = 10) =>
        _history.Where(r => r.AgentId == agentId).Take(limit).ToList().AsReadOnly();

    public void AddRunToHistory(PipelineRun run)
    {
        _history.Insert(0, run.ToSummary());
    }

    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
}
