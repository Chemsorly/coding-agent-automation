using CodingAgent.Api.Browse;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.3b: what the pages browse in the issue and repository providers. The API calls the
/// providers with the stored credentials; the web host only sees the results. Lists follow D3 rule 3
/// (<see cref="IUserAccess.DemandList"/>).
/// </summary>
internal static class UiBrowseEndpoints
{
    public static RouteGroupBuilder MapUiBrowseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/backlog", GetBacklog);
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
}
