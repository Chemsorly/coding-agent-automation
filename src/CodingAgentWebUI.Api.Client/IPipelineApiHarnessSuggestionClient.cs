using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// HTTP client for the harness suggestion endpoints on the Pipeline API.
/// Used by the orchestrator to delegate harness suggestion persistence to the API.
/// </summary>
public interface IPipelineApiHarnessSuggestionClient
{
    Task<HarnessSuggestions?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct = default);
}
