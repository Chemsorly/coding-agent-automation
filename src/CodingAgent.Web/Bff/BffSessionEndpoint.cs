using CodingAgent.Web.Auth;
using CodingAgent.Web.Models;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.Bff;

/// <summary>
/// Spec 052 DD4, Req 3.1: <c>GET /bff/session</c> tells the browser who is signed in and what they
/// may do. Bindings are evaluated here, never in the browser (D5). It needs a signed-in user but
/// no role (the fallback policy), because a no-access user needs it too; that user gets an empty
/// grant and no build details. Without a valid session the cookie handler answers 401. The answer
/// is per user and never cached.
/// </summary>
internal static class BffSessionEndpoint
{
    public const string Path = "/bff/session";

    public static IEndpointRouteBuilder MapBffSession(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, GetAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(
        HttpContext context, IRbacEvaluator rbac, IAuthUiInfo authUi, BuildInfo build, CancellationToken ct)
    {
        var user = context.User;
        var grant = await rbac.EvaluateAsync(user, ct);
        var session = new BffSession(
            Subject: user.FindFirst(AuthClaimTypes.Subject)?.Value ?? "",
            Username: AuthPrincipals.GetUsername(user) ?? "",
            DisplayName: user.FindFirst(AuthClaimTypes.DisplayName)?.Value,
            Email: user.FindFirst(AuthClaimTypes.Email)?.Value,
            Groups: AuthPrincipals.GetGroups(user),
            IdentitySource: user.FindFirst(AuthClaimTypes.IdentitySource)?.Value ?? "",
            Expires: AuthPrincipals.GetSessionExpiry(user) ?? default,
            Grant: grant,
            OidcProviderName: authUi.OidcProviderName,
            Build: grant.HasAnyAccess ? build : null);

        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(session, UiJsonContext.Default.BffSession);
    }
}
