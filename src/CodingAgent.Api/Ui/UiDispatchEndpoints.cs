using CodingAgent.Api.Browse;
using CodingAgent.Api.Dispatch;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.3c: manual dispatch from the AgentCoding drawers, by template and issue or pull request
/// number (D5). The API resolves the providers and the project from the stored template and requires
/// operator on the project that owns it (global operator for a template of no project); an unreadable
/// template is "not found". 200 carries the message the drawer shows; a refusal answers 409 and a
/// provider that fails 502, each with the reason as a JSON string.
/// </summary>
internal static class UiDispatchEndpoints
{
    public static RouteGroupBuilder MapUiDispatchEndpoints(this RouteGroupBuilder group)
    {
        var template = group.MapGroup("/templates/{templateId}").RequireAuthorization(UiPolicies.AnyOperator);
        template.MapPost("/issues/{issueIdentifier}/dispatch", DispatchIssue);
        template.MapPost("/pull-requests/{number:int}/dispatch", DispatchPullRequest);
        template.MapPost("/epics/{issueIdentifier}/dispatch", DispatchEpic);
        return group;
    }

    /// <summary>POST …/issues/{issueIdentifier}/dispatch — the issue, for implementation.</summary>
    internal static Task<IResult> DispatchIssue(
        string templateId, string issueIdentifier, IUserAccess access, TemplateBrowseService browse, ManualDispatchService dispatch, CancellationToken ct) =>
        DispatchAsync(templateId, access, browse, template => dispatch.DispatchIssueAsync(template, issueIdentifier, CancellationToken.None), ct);

    /// <summary>POST …/pull-requests/{number}/dispatch — the open pull request, for review.</summary>
    internal static Task<IResult> DispatchPullRequest(
        string templateId, int number, IUserAccess access, TemplateBrowseService browse, ManualDispatchService dispatch, CancellationToken ct) =>
        DispatchAsync(templateId, access, browse, template => dispatch.DispatchPullRequestAsync(template, number, CancellationToken.None), ct);

    /// <summary>POST …/epics/{issueIdentifier}/dispatch — the epic, for analysis or, once approved, decomposition.</summary>
    internal static Task<IResult> DispatchEpic(
        string templateId, string issueIdentifier, IUserAccess access, TemplateBrowseService browse, ManualDispatchService dispatch, CancellationToken ct) =>
        DispatchAsync(templateId, access, browse, template => dispatch.DispatchEpicAsync(template, issueIdentifier, CancellationToken.None), ct);

    /// <summary>
    /// Finds the template and checks access with the request's token. The dispatch itself runs with
    /// <see cref="CancellationToken.None"/>: once it has started it runs to the end, so a caller that goes
    /// away does not leave a cleared label without its work item.
    /// </summary>
    private static async Task<IResult> DispatchAsync(
        string templateId, IUserAccess access, TemplateBrowseService browse, Func<BrowseTemplate, Task<string>> dispatch, CancellationToken ct)
    {
        var template = await browse.FindAsync(templateId, ct);
        if (template is null)
            return TypedResults.NotFound();
        access.DemandOperate(template.Project?.Id);

        try
        {
            return TypedResults.Ok(await dispatch(template));
        }
        catch (DispatchRefusedException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (TemplateBrowseException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
