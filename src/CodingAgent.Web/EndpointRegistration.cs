using CodingAgent.Web.Auth;
using CodingAgent.Web.Components;

namespace CodingAgent.Web;

/// <summary>
/// Extension methods for configuring middleware pipeline and mapping application endpoints.
/// </summary>
internal static class EndpointRegistration
{
    /// <summary>
    /// Maps all application endpoints: health probes, static files, auth middleware and endpoints,
    /// and Razor components.
    /// </summary>
    public static WebApplication MapApplicationEndpoints(this WebApplication app)
    {
        // First, so the OIDC redirect URI and the Secure cookie flag see the client's https.
        app.UseForwardedHeaders();

        // HTTPS redirect, HSTS and security headers: before static files, so every response has them.
        app.UseSecurityHeaders();

        // Kubernetes-style health probes — anonymous, no auth required
        app.MapHealthEndpoints();

        // Redirect root "/" to the main page (relative redirect — works behind any reverse proxy)
        // TODO [WARNING]: The issue requirement states "keep /agent-coding working as an alias".
        // No alias redirect from /agent-coding to /overview (or vice versa) was added. If existing
        // bookmarks, CI health checks, or external links target /agent-coding expecting the Overview
        // page, they will land on the Pipelines page instead (AgentCoding.razor is still at /agent-coding).
        // Either add an anonymous alias endpoint that redirects to the Overview page,
        // or confirm that /agent-coding intentionally continues to render the Pipelines page.
        app.MapGet("/", () => Results.Redirect("overview"))
            .AllowAnonymous();

        // The run-history JSON export lives in the Pipeline API (/api/export/runs.json); the web
        // host maps no /api endpoints of its own.

        app.UseStaticFiles();
        app.MapStaticAssets().AllowAnonymous();

        // Spec 049: every other endpoint requires a signed-in user (fallback policy). Order:
        // rate limiter (login), authentication, authorization, then antiforgery for form posts.
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapAuthEndpoints();

        // Config import/export endpoints now served by CodingAgent.Api.

        // frame-ancestors is part of the policy UseSecurityHeaders sends on every response.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

        return app;
    }
}
