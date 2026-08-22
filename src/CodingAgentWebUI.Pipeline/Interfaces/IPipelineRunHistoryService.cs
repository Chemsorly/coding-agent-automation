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

    /// <summary>
    /// Persists a pre-serialized run summary directly, bypassing the <see cref="PipelineRun"/> wrapper.
    /// Used by the API endpoint when the orchestrator sends a summary over HTTP rather than writing DB directly.
    /// </summary>
    Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default);

    /// <summary>Retrieves the run history.</summary>
    Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default);

    /// <summary>Retrieves the run history with pagination.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>Retrieves a single pipeline run summary by run ID. Returns null if not found.</summary>
    Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Retrieves paginated run history filtered to runs that have feedback.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="feedbackOnly">When true, returns only runs with non-null Feedback. Filter is applied in the DB query, not after paging.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default);
}
