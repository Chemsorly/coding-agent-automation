using CodingAgent.Api.Browse;
using CodingAgent.Contracts.Ui;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.3b: what the pages browse in the issue and repository providers. The API calls the
/// providers with the stored credentials; the web host only sees the results. The backlog follows D3
/// rule 3 (<see cref="IUserAccess.DemandList"/>); a template's browse needs read on the project that
/// owns the template (global read for a template of no project), and an unreadable template is "not
/// found". A provider that is gone or fails answers 502 with the reason as a JSON string.
/// </summary>
internal static class UiBrowseEndpoints
{
    public static RouteGroupBuilder MapUiBrowseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/backlog", GetBacklog);

        var template = group.MapGroup("/templates/{templateId}");
        template.MapGet("/issues", GetIssues);
        template.MapGet("/issues/readiness", GetIssueReadiness);
        template.MapGet("/labels", GetLabels);
        template.MapGet("/pull-requests", GetPullRequests);
        template.MapGet("/epics", GetEpics);
        template.MapGet("/epic-labels", GetEpicLabels);
        template.MapGet("/active-issues", GetActiveIssues);
        return group;
    }

    /// <summary>
    /// GET /api/ui/backlog?projectId= — the open issues of the enabled templates' issue providers, each
    /// with its dispatch readiness. The Work screen shows them; the Attention screen shows the blocked ones.
    /// </summary>
    internal static async Task<IResult> GetBacklog(IUserAccess access, BacklogService backlog, CancellationToken ct, string? projectId = null)
    {
        access.DemandList(projectId);
        return TypedResults.Ok(await backlog.GetBacklogAsync(projectId, ct));
    }

    /// <summary>GET …/issues?page=&amp;labels= — a page of the template tracker's open issues.</summary>
    internal static Task<IResult> GetIssues(
        string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct,
        int page = 1, [FromQuery] string[]? labels = null) =>
        BrowseAsync(templateId, access, browse, ct, template => browse.ListIssuesAsync(template, page, labels, ct));

    /// <summary>GET …/issues/readiness?page=&amp;labels= — the dependency readiness of that page's issues, by identifier.</summary>
    internal static Task<IResult> GetIssueReadiness(
        string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct,
        int page = 1, [FromQuery] string[]? labels = null) =>
        BrowseAsync(templateId, access, browse, ct, template => browse.CheckReadinessAsync(template, page, labels, ct));

    /// <summary>GET …/labels — the template tracker's labels.</summary>
    internal static Task<IResult> GetLabels(string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct) =>
        BrowseAsync(templateId, access, browse, ct, template => browse.ListLabelsAsync(template, ct));

    /// <summary>GET …/pull-requests?page=&amp;labels= — a page of the template repository's open pull requests.</summary>
    internal static Task<IResult> GetPullRequests(
        string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct,
        int page = 1, [FromQuery] string[]? labels = null) =>
        BrowseAsync(templateId, access, browse, ct, template => browse.ListPullRequestsAsync(template, page, labels, ct));

    /// <summary>GET …/epics?page=&amp;labels= — the new and the approved epics of the epic tracker.</summary>
    internal static Task<IResult> GetEpics(
        string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct,
        int page = 1, [FromQuery] string[]? labels = null) =>
        BrowseAsync(templateId, access, browse, ct, template => browse.ListEpicsAsync(template, page, labels, ct));

    /// <summary>GET …/epic-labels — the epic tracker's labels, without the epic workflow labels.</summary>
    internal static Task<IResult> GetEpicLabels(string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct) =>
        BrowseAsync(templateId, access, browse, ct, template => browse.ListEpicLabelsAsync(template, ct));

    /// <summary>GET …/active-issues — the issues and pull requests of the template's providers that already have work.</summary>
    internal static Task<IResult> GetActiveIssues(string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct) =>
        BrowseAsync(templateId, access, browse, ct, async template =>
            (IReadOnlyList<UiActiveIssue>)(await browse.GetActiveIssuesAsync(template, ct))
                .Select(a => new UiActiveIssue(a.IssueIdentifier, a.IssueProviderConfigId, a.Status))
                .ToList());

    private static async Task<IResult> BrowseAsync<T>(
        string templateId, IUserAccess access, TemplateBrowseService browse, CancellationToken ct, Func<BrowseTemplate, Task<T>> read)
    {
        var template = await browse.FindAsync(templateId, ct);
        if (template is null)
            return TypedResults.NotFound();
        access.DemandRead(template.Project?.Id);

        try
        {
            return TypedResults.Ok(await read(template));
        }
        catch (TemplateBrowseException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
