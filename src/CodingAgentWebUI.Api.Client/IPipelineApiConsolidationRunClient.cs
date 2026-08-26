using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// HTTP client for the consolidation run endpoints on the Pipeline API.
/// Used by the orchestrator to delegate all consolidation run persistence to the API
/// instead of connecting directly to the database.
/// </summary>
public interface IPipelineApiConsolidationRunClient
{
    Task SaveRunAsync(ConsolidationRun run, CancellationToken ct = default);
    Task<IReadOnlyList<ConsolidationRun>> LoadAllRunsAsync(CancellationToken ct = default);
    Task<ConsolidationRun?> GetByIdAsync(string runId, CancellationToken ct = default);
    Task DeleteRunAsync(string runId, CancellationToken ct = default);
}
