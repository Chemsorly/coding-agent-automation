using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace CodingAgent.Api.Ui;

/// <summary>
/// The UI surface of the Pipeline API (spec 051 D1): everything under <c>/api/ui</c> accepts only
/// the user token. The group baseline is <see cref="UiPolicies.AnyAccess"/>; endpoints that need
/// more add a stricter policy, and project checks run in the handler through <see cref="IUserAccess"/>.
/// </summary>
internal static class UiEndpoints
{
    public const string Prefix = "/api/ui";

    public static IEndpointRouteBuilder MapUiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix)
            .RequireAuthorization(UiPolicies.AnyAccess)
            .AddEndpointFilter<UiAccessDeniedFilter>();

        group.MapGet("/me", GetMe);
        group.MapGet("/health", GetHealth);
        group.MapUiConfigEndpoints();
        group.MapUiRunEndpoints();
        group.MapUiBrowseEndpoints();
        group.MapUiDispatchEndpoints();
        group.MapUiAgentEndpoints();
        group.MapUiChatEndpoints();
        group.MapUiConsolidationEndpoints();
        group.MapUiLoopEndpoints();
        group.MapUiSettingsEndpoints();

        return app;
    }

    /// <summary>
    /// GET /api/ui/health — database and Redis state of this replica for the sidebar. Reads cached
    /// state only (the background database monitor, the multiplexer's connection flag).
    /// </summary>
    internal static IResult GetHealth(DatabaseHealthState database, [FromServices] IConnectionMultiplexer? redis) =>
        TypedResults.Ok(new UiHealthResponse(database.IsDatabaseHealthy, redis?.IsConnected));

    /// <summary>GET /api/ui/me — the user and grant from the token, with project names resolved.</summary>
    internal static async Task<IResult> GetMe(IUserAccess access, IProjectStore projects, CancellationToken ct)
    {
        var grant = access.Grant;
        var names = (await projects.LoadProjectsAsync(ct))
            .Where(p => ProjectIds.Normalize(p.Id) is not null)
            .GroupBy(p => ProjectIds.Normalize(p.Id)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var projectRoles = grant.ProjectRoles
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new UiMeProjectRole(
                p.Key,
                names.GetValueOrDefault(p.Key),
                AccessRoleNames.ToName(p.Value),
                names.ContainsKey(p.Key)))
            .ToList();

        return TypedResults.Ok(new UiMeResponse(
            access.Subject,
            access.Username,
            access.IdentitySource,
            AccessRoleNames.ToName(grant.GlobalRole),
            projectRoles));
    }
}
