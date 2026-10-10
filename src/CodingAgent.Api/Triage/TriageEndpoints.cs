using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgent.Api.Triage;

/// <summary>
/// Minimal API endpoints for triages. Operator tier (master key) only: the web host checks the signed-in
/// user's role on the triage's project before calling them, and agent keys never reach them.
/// </summary>
public static class TriageEndpoints
{
    public static void MapTriageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/triages")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/", List);
        group.MapGet("/similar", Similar);
        group.MapGet("/by-run/{runId:guid}", ByRun);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
        group.MapPost("/{id:guid}/rerun", Rerun);
        group.MapPut("/{id:guid}/drafts/{draftId}", UpdateDraft);
        group.MapPost("/{id:guid}/drafts/{draftId}/reset", ResetDraft);
        group.MapPost("/{id:guid}/create-issues", CreateIssues);
        group.MapPost("/{id:guid}/dismiss", Dismiss);
    }

    internal static async Task<IResult> List(
        [FromQuery] string? projectId,
        [FromQuery] string? tab,
        [FromQuery] string? source,
        [FromQuery] string? q,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        TriageService service,
        CancellationToken ct)
    {
        var result = await service.ListAsync(new TriageListQuery
        {
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            Tab = ParseEnum<TriageListTab>(tab) ?? TriageListTab.All,
            Source = ParseEnum<TriageSource>(source),
            Search = q,
            Page = page ?? 1,
            PageSize = pageSize ?? 25,
        }, ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Accepts the JSON name (<c>need_you</c>) and the member name (<c>NeedYou</c>) of a filter value.</summary>
    internal static T? ParseEnum<T>(string? value) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.TryParse<T>(value.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var parsed) ? parsed : null;

    internal static async Task<IResult> Similar(
        [FromQuery] string projectId, [FromQuery] string title, TriageService service, CancellationToken ct) =>
        TypedResults.Ok(await service.FindSimilarAsync(projectId, title ?? "", ct));

    internal static async Task<IResult> ByRun(Guid runId, TriageService service, CancellationToken ct) =>
        await service.FindByRunAsync(runId, ct) is { } id ? TypedResults.Ok(new { id }) : TypedResults.NotFound();

    internal static async Task<IResult> Get(Guid id, TriageService service, CancellationToken ct) =>
        await service.GetAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound();

    internal static Task<IResult> Create([FromBody] CreateTriageRequest request, TriageService service, CancellationToken ct) =>
        Handle(async () =>
        {
            var detail = await service.CreateAsync(request, ct);
            return TypedResults.Created($"/api/triages/{detail.Record.Id}", detail);
        });

    internal static Task<IResult> Rerun(Guid id, [FromBody] RerunTriageRequest request, TriageService service, CancellationToken ct) =>
        Handle(async () => TypedResults.Accepted($"/api/triages/{id}", await service.RerunAsync(id, request, ct)));

    internal static Task<IResult> UpdateDraft(
        Guid id, string draftId, [FromBody] UpdateTriageDraftRequest request, TriageService service, CancellationToken ct) =>
        Handle(async () => TypedResults.Ok(await service.UpdateDraftAsync(id, draftId, request, ct)));

    internal static Task<IResult> ResetDraft(Guid id, string draftId, TriageService service, CancellationToken ct) =>
        Handle(async () => TypedResults.Ok(await service.ResetDraftAsync(id, draftId, ct)));

    internal static Task<IResult> CreateIssues(
        Guid id, [FromBody] CreateTriageIssuesRequest request, TriageIssueCreator creator, CancellationToken ct) =>
        Handle(async () => TypedResults.Ok(await creator.CreateAsync(id, request, ct)));

    internal static Task<IResult> Dismiss(Guid id, [FromBody] DismissTriageRequest request, TriageService service, CancellationToken ct) =>
        Handle(async () => TypedResults.Ok(await service.DismissAsync(id, request, ct)));

    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TriageRequestException ex)
        {
            return TypedResults.Problem(detail: ex.Message, statusCode: ex.StatusCode);
        }
    }
}
