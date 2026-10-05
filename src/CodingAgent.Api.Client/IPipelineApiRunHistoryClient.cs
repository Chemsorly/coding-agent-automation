using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/pipeline-runs endpoint group.
/// </summary>
public interface IPipelineApiRunHistoryClient
{
    /// <summary>
    /// Calls <c>GET /api/pipeline-runs</c>; every <paramref name="query"/> property maps to the query-string key of
    /// the same name. The outcome, project, date and run-type filters are applied DB-side by the API so pagination
    /// stays correct.
    /// </summary>
    /// <param name="query">Paging and filter options; <c>new RunHistoryQuery()</c> gives the endpoint defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(RunHistoryQuery query, CancellationToken ct = default);
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
