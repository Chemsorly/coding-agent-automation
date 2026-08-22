using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

internal sealed class PipelineApiConsolidationRunClient : IPipelineApiConsolidationRunClient
{
    private readonly HttpClient _http;

    public PipelineApiConsolidationRunClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<ConsolidationRun>> LoadAllRunsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ConsolidationRun>>(
            "/api/consolidation-runs",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<ConsolidationRun?> GetByIdAsync(string runId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var guid)) return null;
        var response = await _http.GetAsync($"/api/consolidation-runs/{guid}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConsolidationRun>(PipelineJsonOptions.Default, ct);
    }

    public async Task SaveRunAsync(ConsolidationRun run, CancellationToken ct = default)
    {
        if (!Guid.TryParse(run.RunId, out var guid))
            throw new InvalidOperationException($"ConsolidationRun.RunId '{run.RunId}' is not a valid GUID and cannot be persisted via the API.");
        var response = await _http.PutAsJsonAsync(
            $"/api/consolidation-runs/{guid}",
            run,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var guid))
            throw new InvalidOperationException($"RunId '{runId}' is not a valid GUID and cannot be deleted via the API.");
        var response = await _http.DeleteAsync($"/api/consolidation-runs/{guid}", ct);
        response.EnsureSuccessStatusCode();
    }
}
