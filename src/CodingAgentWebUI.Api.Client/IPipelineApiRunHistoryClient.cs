using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/pipeline-runs endpoint group.
/// </summary>
public interface IPipelineApiRunHistoryClient
{
    /// <param name="finalStep">Optional outcome filter (e.g. <see cref="PipelineStep.Failed"/>); null returns all outcomes. Applied DB-side by the API so pagination stays correct.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page = 1, int pageSize = 50, bool feedbackOnly = false, bool includeActive = false, PipelineStep? finalStep = null, CancellationToken ct = default);
    Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Persists a completed run summary. The API stores the summary in the PipelineRuns table.
    /// Called by the orchestrator on terminal run completion instead of writing directly to DB.
    /// </summary>
    Task AddRunToHistoryAsync(PipelineRunSummary summary, CancellationToken ct = default);
}
