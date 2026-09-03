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

    public Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default)
        => GetPendingAsync(maxResults, null, ct);

    public async Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults, string? projectId, CancellationToken ct = default)
    {
        var url = $"/api/work-items/pending?maxResults={maxResults}";
        if (!string.IsNullOrEmpty(projectId))
            url += $"&projectId={Uri.EscapeDataString(projectId)}";

        var result = await _http.GetFromJsonAsync<List<PendingWorkItemDto>>(
            url,
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

        // 409 Conflict — expected contention: another instance claimed first. Caller should skip.
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            return null;

        // 404 Not Found — unexpected: item was in the pending list but no longer exists.
        // This indicates a data race (deleted between GetPendingAsync and ClaimAsync) or a
        // bug in the pending query. Throw so the caller can log a distinct warning rather
        // than silently treating it as a normal 409 contention case.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new WorkItemNotFoundException(workItemId);

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
        // 409 Conflict — expected: item already in Pending, Running, or terminal state.
        // The requeue intent is satisfied; treat as success (no-op).
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            return;
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
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/work-items");
        if (!string.IsNullOrEmpty(request.RunId))
            req.Headers.Add("X-Idempotency-Key", request.RunId);
        req.Content = JsonContent.Create(request, options: PipelineJsonOptions.Default);
        var response = await _http.SendAsync(req, ct);
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

    public async Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds, string? projectId = null, CancellationToken ct = default)
    {
        var url = $"/api/work-items/active?olderThanSeconds={olderThanSeconds}";
        if (!string.IsNullOrEmpty(projectId))
            url += $"&projectId={Uri.EscapeDataString(projectId)}";

        var result = await _http.GetFromJsonAsync<List<ActiveWorkItemDto>>(
            url,
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

    public async Task<string?> GetK8sJobNameAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/work-items/{workItemId}/k8s-job-name", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<K8sJobNameResponse>(PipelineJsonOptions.Default, ct);
        return result?.JobName;
    }

    public async Task<WorkItemStatus?> GetStatusAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/work-items/{workItemId}/status", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<WorkItemStatusResponse>(PipelineJsonOptions.Default, ct);
        return result?.Status;
    }

    public async Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct = default)
    {
        var url = $"/api/work-items/is-distributed?issueIdentifier={Uri.EscapeDataString(issueIdentifier)}&issueProviderConfigId={Uri.EscapeDataString(issueProviderConfigId)}";
        var result = await _http.GetFromJsonAsync<IsDistributedResponse>(url, PipelineJsonOptions.Default, ct);
        return result?.IsDistributed ?? false;
    }

    public async Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIdentifiersAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ActiveIdentifierDto>>(
            "/api/work-items/active-identifiers",
            PipelineJsonOptions.Default,
            ct);
        if (result is null) return [];
        return result.Select(r => (r.IssueIdentifier, r.IssueProviderConfigId)).ToList();
    }

    // Internal DTOs for response deserialization
    /// <summary>Shape of <c>GET /api/work-items/{id}/retry-count</c>. Positional so the
    /// deserializer assigns through the constructor — an init-only property looks unassigned to
    /// static analysis, since nothing in this codebase ever writes it.</summary>
    private sealed record RetryCountResponse(int RetryCount);

    private sealed record K8sJobNameResponse(string? JobName);

    private sealed record WorkItemStatusResponse(WorkItemStatus Status);

    private sealed record IsDistributedResponse(bool IsDistributed);

    private sealed record ActiveIdentifierDto
    {
        public string IssueIdentifier { get; init; } = "";
        public string IssueProviderConfigId { get; init; } = "";
    }
}
