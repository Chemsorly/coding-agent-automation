using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/pipeline-runs endpoint group.
/// </summary>
public interface IPipelineApiRunHistoryClient
{
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page = 1, int pageSize = 50, bool feedbackOnly = false, bool includeActive = false, CancellationToken ct = default);
    Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default);
}
