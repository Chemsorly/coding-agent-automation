using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiConsolidationWorkItemClient"/> backed by <see cref="HttpClient"/>
/// registered via <see cref="IHttpClientFactory"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP client wrapper — requires integration tests, not unit tests.")]
internal sealed class PipelineApiConsolidationWorkItemClient : IPipelineApiConsolidationWorkItemClient
{
    private readonly HttpClient _http;

    public PipelineApiConsolidationWorkItemClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(
        int maxResults = 50, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PendingWorkItemDto>>(
            $"/api/consolidation-work-items/pending?maxResults={maxResults}",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<ConsolidationWorkItemClaimResponse?> ClaimAsync(
        Guid workItemId, ClaimWorkItemRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/consolidation-work-items/{workItemId}/claim",
            request,
            PipelineJsonOptions.Default,
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            return null;

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new WorkItemNotFoundException(workItemId);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConsolidationWorkItemClaimResponse>(
            PipelineJsonOptions.Default, ct);
    }

    public async Task TransitionRunAsync(
        string runId, ConsolidationRunStatus status, string? summary = null, CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var guid))
            throw new InvalidOperationException($"RunId '{runId}' is not a valid GUID.");

        var body = new { status, summary };
        var response = await _http.PostAsJsonAsync(
            $"/api/consolidation-runs/{guid}/transition",
            body,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RequeueAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/work-items/{workItemId}/requeue", null, ct);
        // 409 Conflict — expected: item already in Pending, Running, or terminal state.
        // The requeue intent is satisfied; treat as success (no-op).
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            return;
        response.EnsureSuccessStatusCode();
    }
}
