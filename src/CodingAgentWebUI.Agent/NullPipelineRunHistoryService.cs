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
    public IReadOnlyList<PipelineRunSummary> GetRunHistory() => [];

    public IReadOnlyList<PipelineRunSummary> GetRunsByAgentId(string agentId, int limit = 10) => [];

    public void AddRunToHistory(PipelineRun run) { }

    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }

    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
}
