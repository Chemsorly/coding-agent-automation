using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiWorkItemClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiWorkItemClient : IPipelineApiWorkItemClient
{
    private readonly HttpClient _http;

    public PipelineApiWorkItemClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PendingWorkItemDto>>(
            $"/api/work-items/pending?maxResults={maxResults}",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<WorkItemClaimResponse?> ClaimAsync(Guid workItemId, ClaimWorkItemRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/claim",
            request,
            PipelineJsonOptions.Default,
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict ||
            response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemClaimResponse>(PipelineJsonOptions.Default, ct);
    }

    public async Task<JobAssignmentMessage?> GetAssignmentAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/work-items/{workItemId}/assignment", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
            response.StatusCode == System.Net.HttpStatusCode.Gone)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JobAssignmentMessage>(PipelineJsonOptions.Default, ct);
    }

    public async Task PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status",
            request,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RequeueAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/work-items/{workItemId}/requeue", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<RetryCountResponse>(
            $"/api/work-items/{workItemId}/retry-count",
            PipelineJsonOptions.Default,
            ct);
        return result?.RetryCount ?? 0;
    }

    public async Task<WorkItemStalenessResult?> GetStalenessAsync(
        string issueIdentifier,
        string issueProviderConfigId,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        var url = $"/api/work-items/staleness?issueIdentifier={Uri.EscapeDataString(issueIdentifier)}&issueProviderConfigId={Uri.EscapeDataString(issueProviderConfigId)}&since={Uri.EscapeDataString(since.ToString("O"))}";
        var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkItemStalenessResult>(PipelineJsonOptions.Default, ct);
    }

    public async Task<Guid> CreateAsync(JobDistributionRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>(cancellationToken: ct);
    }

    public async Task PostLabelSwapAsync(Guid workItemId, string label, CancellationToken ct = default)
    {
        var body = new { label };
        var response = await _http.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/label-swap",
            body,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ActiveWorkItemDto>>(
            $"/api/work-items/active?olderThanSeconds={olderThanSeconds}",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task PostLastProgressAsync(Guid workItemId, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        var body = new { timestamp };
        var response = await _http.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/last-progress",
            body,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }

    // Internal DTO for retry-count response
    private sealed record RetryCountResponse
    {
        public int RetryCount { get; init; }
    }
}
