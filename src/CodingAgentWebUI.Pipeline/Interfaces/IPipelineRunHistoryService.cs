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

    /// <summary>Retrieves paginated run history filtered to runs that have feedback.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="feedbackOnly">When true, returns only runs with non-null Feedback. Filter is applied in the DB query, not after paging.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default);

    /// <summary>
    /// Retrieves paginated run history filtered by outcome (<paramref name="finalStep"/>) in addition to
    /// the optional feedback filter — the DB applies the outcome filter before paging, so pagination stays
    /// correct across the whole history.
    /// </summary>
    /// <param name="finalStep">When set, returns only runs whose terminal step matches (e.g. <see cref="PipelineStep.Failed"/>); null = no outcome filter.</param>
    /// <param name="projectId">When set, returns only runs in that project; null = all projects.</param>
    /// <remarks>
    /// The default implementation ignores <paramref name="finalStep"/> and <paramref name="projectId"/>.
    /// Only the DB-backed <c>PostgresPipelineRunHistoryService</c> (the API endpoint's implementor) overrides
    /// it; the other implementors — the agent's null service and the API-client-backed stores — are not on
    /// the filter path (the web UI's pages call <c>IPipelineApiRunHistoryClient</c> directly).
    /// </remarks>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, CancellationToken ct = default)
        => GetRunHistoryAsync(page, pageSize, feedbackOnly, ct);

    /// <summary>Retrieves a single pipeline run summary by run ID. Returns null if not found.</summary>
    Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default);
}
