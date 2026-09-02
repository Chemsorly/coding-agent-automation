using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/pipeline-runs endpoint group.
/// </summary>
public interface IPipelineApiRunHistoryClient
{
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page = 1, int pageSize = 50, bool feedbackOnly = false, bool includeActive = false, CancellationToken ct = default);
    Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Persists a completed run summary. The API stores the summary in the PipelineRuns table.
    /// Called by the orchestrator on terminal run completion instead of writing directly to DB.
    /// </summary>
    Task AddRunToHistoryAsync(PipelineRunSummary summary, CancellationToken ct = default);

    /// <summary>
    /// Returns the branch names of all currently active (non-terminal) pipeline runs.
    /// Calls <c>GET /api/pipeline-runs/active-branches</c>.
    /// Used by <c>SchedulerRunQueryService</c> to populate the housekeeping active-run guard.
    /// </summary>
    Task<IReadOnlyList<string>> GetActiveBranchesAsync(CancellationToken ct = default);
}
