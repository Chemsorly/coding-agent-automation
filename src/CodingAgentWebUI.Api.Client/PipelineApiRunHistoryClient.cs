using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiRunHistoryClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiRunHistoryClient : IPipelineApiRunHistoryClient
{
    private readonly HttpClient _http;

    public PipelineApiRunHistoryClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(
        int page = 1,
        int pageSize = 50,
        bool feedbackOnly = false,
        bool includeActive = false,
        CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<PipelineRunSummary>>(
            $"/api/pipeline-runs?page={page}&pageSize={pageSize}&feedbackOnly={feedbackOnly}&includeActive={includeActive}",
            PipelineJsonOptions.Default,
            ct);
        return result!;
    }

    public async Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/pipeline-runs/{runId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PipelineRunSummary>(PipelineJsonOptions.Default, ct);
    }

    public async Task AddRunToHistoryAsync(PipelineRunSummary summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        // TODO [WARNING]: HttpRequestMessage is IDisposable but is not disposed here. If SendAsync
        // throws (e.g., TaskCanceledException on timeout), the message and its JsonContent body are
        // never disposed. Fix: use `using var req = new HttpRequestMessage(...)`.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/pipeline-runs/");
        if (!string.IsNullOrEmpty(summary.RunId))
            req.Headers.Add("X-Idempotency-Key", summary.RunId);
        req.Content = JsonContent.Create(summary, options: PipelineJsonOptions.Default);
        var response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
    }
}
