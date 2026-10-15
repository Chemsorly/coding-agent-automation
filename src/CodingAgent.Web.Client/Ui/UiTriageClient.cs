using System.Globalization;
using System.Net.Http.Json;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// The Triage pages (spec 051 2.7). Reading needs read on the triage's project; starting, re-running, editing
/// drafts, creating issues and dismissing need operator on it. The API records who acted from the user's token.
/// A refused request throws <see cref="InvalidOperationException"/> with the API's reason.
/// </summary>
public interface IUiTriageClient
{
    Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct = default);

    /// <summary>The triage, or null when it does not exist or the user cannot see it.</summary>
    Task<TriageDetail?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The id of the triage a run belongs to, or null.</summary>
    Task<Guid?> FindByRunAsync(string runId, CancellationToken ct = default);

    Task<IReadOnlyList<TriageListItem>> FindSimilarAsync(string projectId, string title, CancellationToken ct = default);

    /// <summary>What a triage of the project, started in <paramref name="startInTemplateId"/> (or the default template), would use.</summary>
    Task<TriagePreview> GetPreviewAsync(string projectId, string? startInTemplateId, CancellationToken ct = default);

    Task<TriageDetail> CreateAsync(CreateTriageRequest request, CancellationToken ct = default);

    Task<TriageDetail> RerunAsync(Guid id, RerunTriageRequest request, CancellationToken ct = default);

    Task<TriageDetail> UpdateDraftAsync(Guid id, string draftId, UpdateTriageDraftRequest request, CancellationToken ct = default);

    Task<TriageDetail> ResetDraftAsync(Guid id, string draftId, CancellationToken ct = default);

    Task<CreateTriageIssuesResult> CreateIssuesAsync(Guid id, CreateTriageIssuesRequest request, CancellationToken ct = default);

    Task<TriageDetail> DismissAsync(Guid id, DismissTriageRequest request, CancellationToken ct = default);
}

public sealed class UiTriageClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiTriageClient
{
    private const string Triages = "/api/ui/triages";

    public Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new List<string>
        {
            $"tab={query.Tab}",
            $"page={query.Page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={query.PageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        if (query.ProjectId is not null)
            parameters.Add($"projectId={Uri.EscapeDataString(query.ProjectId)}");
        if (query.Source is { } source)
            parameters.Add($"source={source}");
        if (!string.IsNullOrWhiteSpace(query.Search))
            parameters.Add($"q={Uri.EscapeDataString(query.Search)}");
        return GetAsync($"{Triages}?{string.Join('&', parameters)}", UiJsonContext.Default.TriageListPage, ct);
    }

    public Task<TriageDetail?> GetAsync(Guid id, CancellationToken ct = default) =>
        FindAsync($"{Triages}/{id:D}", UiJsonContext.Default.TriageDetail, ct);

    public async Task<Guid?> FindByRunAsync(string runId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var id))
            return null;
        return (await FindAsync($"{Triages}/by-run/{id:D}", UiJsonContext.Default.UiTriageRef, ct))?.Id;
    }

    public Task<IReadOnlyList<TriageListItem>> FindSimilarAsync(string projectId, string title, CancellationToken ct = default) =>
        GetAsync(
            $"{Triages}/similar?projectId={Uri.EscapeDataString(projectId)}&title={Uri.EscapeDataString(title)}",
            UiJsonContext.Default.IReadOnlyListTriageListItem, ct);

    public Task<TriagePreview> GetPreviewAsync(string projectId, string? startInTemplateId, CancellationToken ct = default) =>
        GetAsync(
            $"{Triages}/preview?projectId={Uri.EscapeDataString(projectId)}"
                + (string.IsNullOrEmpty(startInTemplateId) ? "" : $"&startIn={Uri.EscapeDataString(startInTemplateId)}"),
            UiJsonContext.Default.TriagePreview, ct);

    public Task<TriageDetail> CreateAsync(CreateTriageRequest request, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Post, Triages,
            JsonContent.Create(request, UiJsonContext.Default.CreateTriageRequest), UiJsonContext.Default.TriageDetail, ct);

    public Task<TriageDetail> RerunAsync(Guid id, RerunTriageRequest request, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Post, $"{Triages}/{id:D}/rerun",
            JsonContent.Create(request, UiJsonContext.Default.RerunTriageRequest), UiJsonContext.Default.TriageDetail, ct);

    public Task<TriageDetail> UpdateDraftAsync(Guid id, string draftId, UpdateTriageDraftRequest request, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Put, $"{Triages}/{id:D}/drafts/{Uri.EscapeDataString(draftId)}",
            JsonContent.Create(request, UiJsonContext.Default.UpdateTriageDraftRequest), UiJsonContext.Default.TriageDetail, ct);

    public Task<TriageDetail> ResetDraftAsync(Guid id, string draftId, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Post, $"{Triages}/{id:D}/drafts/{Uri.EscapeDataString(draftId)}/reset",
            null, UiJsonContext.Default.TriageDetail, ct);

    public Task<CreateTriageIssuesResult> CreateIssuesAsync(Guid id, CreateTriageIssuesRequest request, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Post, $"{Triages}/{id:D}/create-issues",
            JsonContent.Create(request, UiJsonContext.Default.CreateTriageIssuesRequest), UiJsonContext.Default.CreateTriageIssuesResult, ct);

    public Task<TriageDetail> DismissAsync(Guid id, DismissTriageRequest request, CancellationToken ct = default) =>
        SendForResultAsync(HttpMethod.Post, $"{Triages}/{id:D}/dismiss",
            JsonContent.Create(request, UiJsonContext.Default.DismissTriageRequest), UiJsonContext.Default.TriageDetail, ct);
}

/// <summary>The body of <c>GET /api/ui/triages/by-run/{runId}</c>.</summary>
public sealed record UiTriageRef(Guid Id);
