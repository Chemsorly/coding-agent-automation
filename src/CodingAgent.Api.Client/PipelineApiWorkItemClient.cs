using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api.Client;

/// <summary>
/// <see cref="IPipelineApiWorkItemClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiWorkItemClient : IPipelineApiWorkItemClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public PipelineApiWorkItemClient(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = (logger ?? Serilog.Log.Logger).ForContext<PipelineApiWorkItemClient>();
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

    public async Task<JobAssignmentMessage?> GetAssignmentAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/work-items/{workItemId}/assignment", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
            response.StatusCode == System.Net.HttpStatusCode.Gone)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JobAssignmentMessage>(PipelineJsonOptions.Default, ct);
    }

    public async Task<bool> PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status",
            request,
            PipelineJsonOptions.Default,
            ct);
        // HTTP 204 No Content = idempotent no-op (already-terminal item); signal to callers.
        // NOTE: the 204 check is intentionally placed BEFORE EnsureSuccessStatusCode() so that
        // 204 is excluded from EnsureSuccessStatusCode's success path and mapped to false. Do not
        // reorder these two statements — any future non-204 2xx added here must be handled explicitly
        // or it will fall through to EnsureSuccessStatusCode and be treated as a real transition (true).
        // TODO: consider a defensive explicit check (e.g. else if (response.IsSuccessStatusCode) return true;
        // with a throw on unrecognised 2xx) to make the contract explicit and prevent silent
        // mis-classification if a proxy/gateway injects an unexpected 2xx. See review finding
        // (DotNetSpecialist) for issue #2802.
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<WorkItemStalenessResult?> GetStalenessAsync(
        IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderConfigId,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        // TODO: Use issueIdentifier.Value instead of the implicit string conversion for consistency with
        // issueProviderConfigId.Value below. If the IssueIdentifier → string implicit conversion is removed
        // in the planned Phase 2 migration, this line will fail to compile while the .Value form would not.
        var url = $"/api/work-items/staleness?issueIdentifier={Uri.EscapeDataString(issueIdentifier)}&issueProviderConfigId={Uri.EscapeDataString(issueProviderConfigId.Value)}&since={Uri.EscapeDataString(since.ToString("O"))}";
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
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            throw new WorkItemRefusedException(await RefusalReasonAsync(response, ct));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>(cancellationToken: ct);
    }

    /// <summary>The text shown when a 422 carries no ProblemDetails <c>detail</c> (a previous-release API).</summary>
    internal const string RefusedWithoutReason = "The API refused the work item (422)";

    /// <summary>
    /// The ProblemDetails <c>detail</c> of a refused create (spec 053 DD2), or <see cref="RefusedWithoutReason"/> when the
    /// body has none: a previous-release API replica answers a consolidation it cannot route with a bare 422.
    /// </summary>
    private static async Task<string> RefusalReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detail.GetString())
                    ? detail.GetString()!
                    : RefusedWithoutReason;
        }
        catch (JsonException)
        {
            return RefusedWithoutReason;
        }
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

    public async Task<WorkItemStatus?> GetStatusAsync(Guid workItemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/work-items/{workItemId}/status", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<WorkItemStatusResponse>(PipelineJsonOptions.Default, ct);
        return result?.Status;
    }

    public async Task<bool> IsIssueDistributedAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct = default)
    {
        // TODO: Use issueIdentifier.Value instead of the implicit string conversion for consistency with
        // issueProviderConfigId.Value below. If the IssueIdentifier → string implicit conversion is removed
        // in the planned Phase 2 migration, this line will fail to compile while the .Value form would not.
        var url = $"/api/work-items/is-distributed?issueIdentifier={Uri.EscapeDataString(issueIdentifier)}&issueProviderConfigId={Uri.EscapeDataString(issueProviderConfigId.Value)}";
        var result = await _http.GetFromJsonAsync<IsDistributedResponse>(url, PipelineJsonOptions.Default, ct);
        return result?.IsDistributed ?? false;
    }

    public async Task SetPriorityAsync(Guid workItemId, int priorityWeight, CancellationToken ct = default)
    {
        var body = new { priorityWeight };
        var response = await _http.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/priority",
            body,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
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

    public async Task<int> GetActiveDecompositionCountAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<ActiveDecompositionCountResponse>(
            "/api/work-items/active-decomposition-count",
            PipelineJsonOptions.Default,
            ct);
        if (result is null)
        {
            _logger.Warning(
                "GetActiveDecompositionCountAsync: response from {Endpoint} deserialized to null — " +
                "the API returned a syntactically valid 200 body that could not be mapped to {Type}; " +
                "throwing to prevent a silent zero from bypassing the concurrent-decomposition limit.",
                "/api/work-items/active-decomposition-count",
                nameof(ActiveDecompositionCountResponse));
            throw new InvalidOperationException(
                "GetActiveDecompositionCountAsync: GetFromJsonAsync returned null for " +
                "/api/work-items/active-decomposition-count. The API response is syntactically valid " +
                "JSON but could not be deserialized into ActiveDecompositionCountResponse.");
        }
        return result.Count;
    }

    public async Task<DispatchPendingResult> DispatchPendingAsync(Guid workItemId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            $"/api/work-items/{workItemId}/dispatch",
            null,
            ct);
        // 503 = transient failure (PVC exhausted, lock timeout, K8s failure — retry next poll cycle).
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            return DispatchPendingResult.Transient;

        // 409 is no longer expected from this endpoint (removed in issue #2976). Any unexpected
        // status codes surface as HttpRequestException so they are visible in logs and traces.
        // TODO [WARNING]: EnsureSuccessStatusCode() on a non-2xx response (e.g. from a misbehaving
        // reverse proxy) throws HttpRequestException without draining the response body. For
        // connection-pooled HttpClient, an unread body can leave the connection in an unclean state,
        // preventing connection reuse and causing pool exhaustion under sustained error conditions.
        // Consider reading/discarding the body before re-throwing, or switching to
        // response.Content.ReadFromJsonAsync which drains the stream regardless.
        response.EnsureSuccessStatusCode();

        // 200 = either dispatched or deferred for an expected reason.
        // Body must be read INSIDE the using scope — the response stream is disposed on scope exit.
        // dispatched:true  → Dispatched (K8s Job created, WorkItem=Dispatched)
        // dispatched:false → PermanentRejection (item not Pending, concurrency limit, no template)
        var dto = await response.Content.ReadFromJsonAsync<DispatchPendingResponse>(
            PipelineJsonOptions.Default, ct);
        // TODO [WARNING]: If dto is null (empty body, wrong Content-Type, or malformed JSON),
        // this silently maps to PermanentRejection, stopping dispatch for this selector for the
        // cycle. A garbled success body is treated as backpressure rather than surfacing as an
        // error, which could mask a server/serialization bug. Consider treating a null dto as
        // Transient (retry next cycle) or throwing, to make the failure mode visible.
        return dto?.Dispatched == true
            ? DispatchPendingResult.Dispatched
            : DispatchPendingResult.PermanentRejection;
    }

    // Internal DTOs for response deserialization
    private sealed record WorkItemStatusResponse(WorkItemStatus Status);

    private sealed record IsDistributedResponse(bool IsDistributed);

    private sealed record ActiveIdentifierDto
    {
        public string IssueIdentifier { get; init; } = "";
        public string IssueProviderConfigId { get; init; } = "";
    }

    /// <summary>
    /// Shape of <c>POST /api/work-items/{id}/dispatch</c> 200 response body.
    /// <c>Dispatched=true</c> means the item was dispatched; <c>false</c> means it was deferred
    /// for an expected reason (concurrency limit, not Pending, no template).
    /// </summary>
    private sealed record DispatchPendingResponse(bool Dispatched, string? Reason);

    private sealed record ActiveDecompositionCountResponse(int Count);
}
