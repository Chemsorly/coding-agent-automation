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
        PipelineStep? finalStep = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var url = $"/api/pipeline-runs?page={page}&pageSize={pageSize}&feedbackOnly={feedbackOnly}&includeActive={includeActive}";
        if (finalStep is { } step)
            url += $"&finalStep={step}";
        if (!string.IsNullOrEmpty(projectId))
            url += $"&projectId={Uri.EscapeDataString(projectId)}";

        var result = await _http.GetFromJsonAsync<PagedResult<PipelineRunSummary>>(
            url,
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
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/pipeline-runs/");
        if (!string.IsNullOrEmpty(summary.RunId))
            req.Headers.Add("X-Idempotency-Key", summary.RunId);
        req.Content = JsonContent.Create(summary, options: PipelineJsonOptions.Default);
        var response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> GetActiveBranchesAsync(CancellationToken ct = default)
    {
        // Use GetAsync + EnsureSuccessStatusCode so that non-2xx responses (403 Forbidden,
        // 500 Internal Server Error, etc.) throw an HttpRequestException instead of being
        // silently swallowed as an empty list. The exception propagates to
        // SchedulerRunQueryService.GetActiveRunBranchesAsync, which propagates it further to
        // HousekeepingService.ExecuteAsync where the catch block sets activeRunBranchesUnavailable=true
        // and applies the conservative fallback (skip all branch updates this cycle).
        // A silent empty list would be indistinguishable from "no active runs" and would defeat
        // the conservative fallback — e.g. a 403 from a misconfigured auth key would cause
        // UpdatePullRequestBranchAsync to be called on live-run branches.
        // NOTE: GetRunHistoryAsync (line ~22) uses the same GetFromJsonAsync pattern and
        //   would throw a NullReferenceException on non-2xx responses (result! dereference).
        //   That is a pre-existing issue in this file; consider aligning it to GetAsync +
        //   EnsureSuccessStatusCode for consistency.
        var response = await _http.GetAsync("/api/pipeline-runs/active-branches", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }
}
