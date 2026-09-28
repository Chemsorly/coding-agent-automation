using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/pipeline-runs endpoint group.
/// </summary>
public interface IPipelineApiRunHistoryClient
{
    /// <param name="finalStep">Optional outcome filter (e.g. <see cref="PipelineStep.Failed"/>); null returns all outcomes. Applied DB-side by the API so pagination stays correct.</param>
    /// <param name="projectId">Optional project scope; null returns all projects. Applied DB-side by the API.</param>
    /// <param name="since">Optional start-date filter; when set, only runs with StartedAt &gt;= this value are returned. Applied DB-side. Pass null for no date filter ("All" window).</param>
    /// <param name="runType">Optional run type filter (e.g. <see cref="PipelineRunType.Consolidation"/>); null returns all types. Applied DB-side by the API so pagination stays correct.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page = 1, int pageSize = 50, bool feedbackOnly = false, bool includeActive = false, PipelineStep? finalStep = null, string? projectId = null, DateTimeOffset? since = null, PipelineRunType? runType = null, CancellationToken ct = default);
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
