using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

internal sealed class PipelineApiHarnessSuggestionClient : IPipelineApiHarnessSuggestionClient
{
    private readonly HttpClient _http;

    public PipelineApiHarnessSuggestionClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<HarnessSuggestions?> GetAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/harness-suggestions", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HarnessSuggestions>(PipelineJsonOptions.Default, ct);
    }

    public async Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(
            "/api/harness-suggestions",
            suggestions,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }
}
