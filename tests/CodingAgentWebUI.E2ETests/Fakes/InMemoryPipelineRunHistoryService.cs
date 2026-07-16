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

    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
    {
        _history.Insert(0, run.ToSummary());
        return Task.CompletedTask;
    }

    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
}
