using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// What the pages browse in the providers (spec 051 2.3b). The API calls the providers with the
/// stored credentials. Lists without a project need global read; a scoped user's pages always pass one.
/// A template's browse needs read on its project. A refusal, or a provider that is gone or failed,
/// throws <see cref="InvalidOperationException"/> with the message to show.
/// </summary>
public interface IUiBrowseClient
{
    /// <summary>The open issues of the enabled templates' issue providers, each with its dispatch readiness.</summary>
    Task<BacklogResult> GetBacklogAsync(string? projectId, CancellationToken ct = default);

    /// <summary>A page of the template tracker's open issues, filtered by any of <paramref name="labels"/>.</summary>
    Task<PagedResult<IssueSummary>> GetIssuesAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default);

    /// <summary>The dependency readiness of the same page's issues, by identifier.</summary>
    Task<IReadOnlyDictionary<string, DependencyCheckResult>> GetIssueReadinessAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default);

    /// <summary>The labels of the template's issue tracker.</summary>
    Task<IReadOnlyList<string>> GetLabelsAsync(string templateId, CancellationToken ct = default);

    /// <summary>A page of the template repository's open pull requests.</summary>
    Task<PagedResult<PullRequestSummary>> GetPullRequestsAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default);

    /// <summary>A page of the new and the approved epics of the template's epic tracker.</summary>
    Task<PagedResult<IssueSummary>> GetEpicsAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default);

    /// <summary>The epic tracker's labels, without the epic workflow labels.</summary>
    Task<IReadOnlyList<string>> GetEpicLabelsAsync(string templateId, CancellationToken ct = default);

    /// <summary>The issues and pull requests of the template's providers that already have work.</summary>
    Task<IReadOnlyList<UiActiveIssue>> GetActiveIssuesAsync(string templateId, CancellationToken ct = default);
}

public sealed class UiBrowseClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiBrowseClient
{
    public Task<BacklogResult> GetBacklogAsync(string? projectId, CancellationToken ct = default) =>
        GetAsync(string.IsNullOrEmpty(projectId) ? "/api/ui/backlog" : $"/api/ui/backlog?projectId={Uri.EscapeDataString(projectId)}",
            UiJsonContext.Default.BacklogResult, ct);

    public Task<PagedResult<IssueSummary>> GetIssuesAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default) =>
        ReadAsync(Paged(templateId, "issues", page, labels), UiJsonContext.Default.PagedResultIssueSummary, ct);

    public Task<IReadOnlyDictionary<string, DependencyCheckResult>> GetIssueReadinessAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default) =>
        ReadAsync(Paged(templateId, "issues/readiness", page, labels), UiJsonContext.Default.IReadOnlyDictionaryStringDependencyCheckResult, ct);

    public Task<IReadOnlyList<string>> GetLabelsAsync(string templateId, CancellationToken ct = default) =>
        ReadAsync($"{Template(templateId)}/labels", UiJsonContext.Default.IReadOnlyListString, ct);

    public Task<PagedResult<PullRequestSummary>> GetPullRequestsAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default) =>
        ReadAsync(Paged(templateId, "pull-requests", page, labels), UiJsonContext.Default.PagedResultPullRequestSummary, ct);

    public Task<PagedResult<IssueSummary>> GetEpicsAsync(string templateId, int page, IReadOnlyList<string>? labels, CancellationToken ct = default) =>
        ReadAsync(Paged(templateId, "epics", page, labels), UiJsonContext.Default.PagedResultIssueSummary, ct);

    public Task<IReadOnlyList<string>> GetEpicLabelsAsync(string templateId, CancellationToken ct = default) =>
        ReadAsync($"{Template(templateId)}/epic-labels", UiJsonContext.Default.IReadOnlyListString, ct);

    public Task<IReadOnlyList<UiActiveIssue>> GetActiveIssuesAsync(string templateId, CancellationToken ct = default) =>
        ReadAsync($"{Template(templateId)}/active-issues", UiJsonContext.Default.IReadOnlyListUiActiveIssue, ct);

    private Task<T> ReadAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) =>
        SendForResultAsync(HttpMethod.Get, path, null, type, ct);

    private static string Template(string templateId) => $"/api/ui/templates/{Uri.EscapeDataString(templateId)}";

    private static string Paged(string templateId, string what, int page, IReadOnlyList<string>? labels) =>
        $"{Template(templateId)}/{what}?page={page}" + string.Concat((labels ?? []).Select(l => $"&labels={Uri.EscapeDataString(l)}"));
}
