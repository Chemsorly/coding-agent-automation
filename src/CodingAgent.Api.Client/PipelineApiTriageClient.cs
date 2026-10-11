using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

internal sealed class PipelineApiTriageClient : IPipelineApiTriageClient
{
    private const string BasePath = "/api/triages";

    private readonly HttpClient _http;

    public PipelineApiTriageClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new List<string>
        {
            $"tab={Enum(query.Tab)}",
            $"page={query.Page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={query.PageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        if (query.ProjectId is not null)
            parameters.Add($"projectId={Uri.EscapeDataString(query.ProjectId)}");
        if (query.Source is { } source)
            parameters.Add($"source={Enum(source)}");
        if (!string.IsNullOrWhiteSpace(query.Search))
            parameters.Add($"q={Uri.EscapeDataString(query.Search)}");

        var response = await _http.GetAsync($"{BasePath}?{string.Join('&', parameters)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TriageListPage>(PipelineJsonOptions.Default, ct) ?? new TriageListPage();
    }

    public async Task<TriageDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{BasePath}/{id:D}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default, ct);
    }

    public async Task<Guid?> FindByRunAsync(string runId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var id))
            return null;
        var response = await _http.GetAsync($"{BasePath}/by-run/{id:D}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ByRunResponse>(PipelineJsonOptions.Default, ct);
        return body?.Id;
    }

    public async Task<IReadOnlyList<TriageListItem>> FindSimilarAsync(string projectId, string title, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"{BasePath}/similar?projectId={Uri.EscapeDataString(projectId)}&title={Uri.EscapeDataString(title)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<TriageListItem>>(PipelineJsonOptions.Default, ct) ?? [];
    }

    public Task<TriageDetail> CreateAsync(CreateTriageRequest request, CancellationToken ct = default) =>
        SendAsync<TriageDetail>(HttpMethod.Post, BasePath, request, ct);

    public Task<TriageDetail> RerunAsync(Guid id, RerunTriageRequest request, CancellationToken ct = default) =>
        SendAsync<TriageDetail>(HttpMethod.Post, $"{BasePath}/{id:D}/rerun", request, ct);

    public Task<TriageDetail> UpdateDraftAsync(Guid id, string draftId, UpdateTriageDraftRequest request, CancellationToken ct = default) =>
        SendAsync<TriageDetail>(HttpMethod.Put, $"{BasePath}/{id:D}/drafts/{Uri.EscapeDataString(draftId)}", request, ct);

    public Task<TriageDetail> ResetDraftAsync(Guid id, string draftId, CancellationToken ct = default) =>
        SendAsync<TriageDetail>(HttpMethod.Post, $"{BasePath}/{id:D}/drafts/{Uri.EscapeDataString(draftId)}/reset", null, ct);

    public Task<CreateTriageIssuesResult> CreateIssuesAsync(Guid id, CreateTriageIssuesRequest request, CancellationToken ct = default) =>
        SendAsync<CreateTriageIssuesResult>(HttpMethod.Post, $"{BasePath}/{id:D}/create-issues", request, ct);

    public Task<TriageDetail> DismissAsync(Guid id, DismissTriageRequest request, CancellationToken ct = default) =>
        SendAsync<TriageDetail>(HttpMethod.Post, $"{BasePath}/{id:D}/dismiss", request, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, path);
        if (body is not null)
            message.Content = JsonContent.Create(body, body.GetType(), options: PipelineJsonOptions.Default);

        var response = await _http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            throw new TriageApiException((int)response.StatusCode, await ReadProblemAsync(response, ct));

        return await response.Content.ReadFromJsonAsync<T>(PipelineJsonOptions.Default, ct)
            ?? throw new TriageApiException((int)response.StatusCode, "The API returned no content");
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException)
        {
            // Not a problem document
        }
        return $"The request failed ({(int)response.StatusCode})";
    }

    private static string Enum<T>(T value) where T : struct, System.Enum =>
        JsonSerializer.Serialize(value, PipelineJsonOptions.Default).Trim('"');

    private sealed record ByRunResponse(Guid Id);
}
