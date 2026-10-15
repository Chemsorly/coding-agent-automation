using CodingAgent.Api.Triage;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.7: the Triage pages. Lists and reads need read on the project (a list without a project needs
/// global read, D3 rule 3); starting, re-running, editing drafts, creating issues and dismissing need operator
/// on the project. Every call that names a triage is checked against the project of the stored triage, so a
/// triage the user cannot see answers 404. The API records who acted from the token, whatever the request body
/// says. A refusal answers 400, 404, 409 or 502 with the reason as a JSON string.
/// </summary>
internal static class UiTriageEndpoints
{
    public static RouteGroupBuilder MapUiTriageEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/triages", List);
        group.MapGet("/triages/similar", Similar);
        group.MapGet("/triages/preview", Preview).RequireAuthorization(UiPolicies.AnyOperator);
        group.MapGet("/triages/by-run/{runId:guid}", ByRun);
        group.MapGet("/triages/{id:guid}", Get);
        group.MapPost("/triages", Create).RequireAuthorization(UiPolicies.AnyOperator);

        var triage = group.MapGroup("/triages/{id:guid}").RequireAuthorization(UiPolicies.AnyOperator);
        triage.MapPost("/rerun", Rerun);
        triage.MapPut("/drafts/{draftId}", UpdateDraft);
        triage.MapPost("/drafts/{draftId}/reset", ResetDraft);
        triage.MapPost("/create-issues", CreateIssues);
        triage.MapPost("/dismiss", Dismiss);
        return group;
    }

    /// <summary>GET /api/ui/triages — one page of a project's triages, or all projects' for a global reader.</summary>
    internal static async Task<IResult> List(
        [AsParameters] TriageListParameters query, IUserAccess access, TriageService service, CancellationToken ct)
    {
        var (projectId, tab, source, q, page, pageSize) = query;
        projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
        access.DemandList(projectId);
        return TypedResults.Ok(await service.ListAsync(new TriageListQuery
        {
            ProjectId = projectId,
            Tab = ParseEnum<TriageListTab>(tab) ?? TriageListTab.All,
            Source = ParseEnum<TriageSource>(source),
            Search = q,
            Page = page ?? 1,
            PageSize = pageSize ?? 25,
        }, ct));
    }

    /// <summary>GET /api/ui/triages/similar — the project's triages with a title like this one.</summary>
    internal static async Task<IResult> Similar(
        [FromQuery] string projectId, [FromQuery] string? title, IUserAccess access, TriageService service, CancellationToken ct)
    {
        access.DemandRead(projectId);
        return TypedResults.Ok(await service.FindSimilarAsync(projectId, title ?? "", ct));
    }

    /// <summary>GET /api/ui/triages/preview — what a triage of the project, started in the given template, would use.</summary>
    internal static async Task<IResult> Preview(
        [FromQuery] string projectId, [FromQuery] string? startIn, IUserAccess access, [AsParameters] TriagePreviewStores stores,
        CancellationToken ct)
    {
        var (projects, providers, profiles, config) = stores;
        access.DemandOperate(projectId);
        var project = await projects.GetProjectByIdAsync(projectId, ct);
        if (project is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(TriagePreviewBuilder.Build(
            project,
            await projects.LoadTemplatesForProjectAsync(projectId, ct),
            string.IsNullOrEmpty(startIn) ? null : startIn,
            await providers.LoadProviderConfigsAsync(ProviderKind.Repository, ct),
            await profiles.LoadAgentProfilesAsync(ct),
            await config.LoadPipelineConfigAsync(ct)));
    }

    /// <summary>GET /api/ui/triages/by-run/{runId} — the triage a run belongs to, as <c>{ id }</c>.</summary>
    internal static async Task<IResult> ByRun(Guid runId, IUserAccess access, TriageService service, CancellationToken ct)
    {
        if (await service.FindByRunAsync(runId, ct) is not { } id || await service.GetAsync(id, ct) is not { } detail)
            return TypedResults.NotFound();
        access.DemandRead(detail.Record.ProjectId);
        return TypedResults.Ok(new { id });
    }

    /// <summary>GET /api/ui/triages/{id}</summary>
    internal static async Task<IResult> Get(Guid id, IUserAccess access, TriageService service, CancellationToken ct)
    {
        if (await service.GetAsync(id, ct) is not { } detail)
            return TypedResults.NotFound();
        access.DemandRead(detail.Record.ProjectId);
        return TypedResults.Ok(detail);
    }

    /// <summary>POST /api/ui/triages — starts an operator triage in a project the user operates.</summary>
    internal static Task<IResult> Create([FromBody] CreateTriageRequest request, IUserAccess access, TriageService service, CancellationToken ct)
    {
        access.DemandOperate(request.ProjectId);
        return RefusalAsJsonAsync(async () =>
            TypedResults.Ok(await service.CreateAsync(request with { RequestedBy = Actor(access) }, ct)));
    }

    /// <summary>POST /api/ui/triages/{id}/rerun</summary>
    internal static Task<IResult> Rerun(Guid id, [FromBody] RerunTriageRequest request, IUserAccess access, TriageService service, CancellationToken ct) =>
        OnOperableTriageAsync(id, access, service,
            async () => TypedResults.Ok(await service.RerunAsync(id, request with { Author = Actor(access) }, ct)), ct);

    /// <summary>PUT /api/ui/triages/{id}/drafts/{draftId}</summary>
    internal static Task<IResult> UpdateDraft(
        Guid id, string draftId, [FromBody] UpdateTriageDraftRequest request, IUserAccess access, TriageService service, CancellationToken ct) =>
        OnOperableTriageAsync(id, access, service,
            async () => TypedResults.Ok(await service.UpdateDraftAsync(id, draftId, request with { EditedBy = Actor(access) }, ct)), ct);

    /// <summary>POST /api/ui/triages/{id}/drafts/{draftId}/reset</summary>
    internal static Task<IResult> ResetDraft(Guid id, string draftId, IUserAccess access, TriageService service, CancellationToken ct) =>
        OnOperableTriageAsync(id, access, service,
            async () => TypedResults.Ok(await service.ResetDraftAsync(id, draftId, ct)), ct);

    /// <summary>POST /api/ui/triages/{id}/create-issues</summary>
    internal static Task<IResult> CreateIssues(
        Guid id, [FromBody] CreateTriageIssuesRequest request, IUserAccess access, TriageService service, TriageIssueCreator creator, CancellationToken ct) =>
        OnOperableTriageAsync(id, access, service,
            async () => TypedResults.Ok(await creator.CreateAsync(id, request with { CreatedBy = Actor(access) }, ct)), ct);

    /// <summary>POST /api/ui/triages/{id}/dismiss</summary>
    internal static Task<IResult> Dismiss(Guid id, [FromBody] DismissTriageRequest request, IUserAccess access, TriageService service, CancellationToken ct) =>
        OnOperableTriageAsync(id, access, service,
            async () => TypedResults.Ok(await service.DismissAsync(id, request with { By = Actor(access) }, ct)), ct);

    /// <summary>Accepts the JSON name (<c>need_you</c>) and the member name (<c>NeedYou</c>) of a filter value.</summary>
    internal static T? ParseEnum<T>(string? value) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Enum.TryParse<T>(value.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var parsed) ? parsed : null;
    }

    /// <summary>The name the triage records for the user: their username, else their subject.</summary>
    private static string Actor(IUserAccess access) => access.Username ?? access.Subject ?? "unknown";

    /// <summary>Runs <paramref name="action"/> when the stored triage exists and the user operates its project.</summary>
    private static async Task<IResult> OnOperableTriageAsync(
        Guid id, IUserAccess access, TriageService service, Func<Task<IResult>> action, CancellationToken ct)
    {
        if (await service.GetAsync(id, ct) is not { } detail)
            return TypedResults.NotFound();
        access.DemandOperate(detail.Record.ProjectId);
        return await RefusalAsJsonAsync(action);
    }

    private static async Task<IResult> RefusalAsJsonAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TriageRequestException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: ex.StatusCode);
        }
    }
}

/// <summary>The query of <c>GET /api/ui/triages</c>.</summary>
internal sealed record TriageListParameters(
    string? ProjectId, string? Tab, string? Source,
    string? Q, int? Page, int? PageSize);

/// <summary>The stores a triage preview is built from.</summary>
internal sealed record TriagePreviewStores(
    IProjectStore Projects, IProviderConfigStore Providers, IAgentProfileStore Profiles, IPipelineConfigStore Config);
