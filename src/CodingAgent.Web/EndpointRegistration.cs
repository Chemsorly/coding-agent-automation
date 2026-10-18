using CodingAgent.Web.Bff;
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
        app.Use(UseHttpSchemeForWebSockets);

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

        // Spec 052 DD7: the path, CSRF and hub-origin rules run before authentication, so a refused
        // request is never forwarded and gets the same status with or without a session (Req 2.11).
        // The WebSockets middleware makes HttpContext.WebSockets recognise an upgrade (HTTP/1.1 and
        // HTTP/2) for the guard; YARP still proxies the upgrade itself.
        app.UseWebSockets();
        app.UseWhen(context => BffRequestGuard.AppliesTo(context.Request.Path), guarded => guarded.Use(BffRequestGuard.InvokeAsync));

        // Spec 049: every other endpoint requires a signed-in user (fallback policy). Order:
        // rate limiter (login), authentication, authorization, then antiforgery for form posts.
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapAuthEndpoints();

        // Spec 052: the session endpoint and the two forwarders to the Pipeline API.
        app.MapBffSession();
        app.MapBffForwarders(app.Configuration.GetValue<string>("PipelineApi:BaseUrl")!);

        // Config import/export endpoints now served by CodingAgent.Api.

        // Spec 052: the routed pages run in the browser as WebAssembly (CodingAgent.Web.Client); the static
        // login page stays in this assembly. frame-ancestors is part of the policy UseSecurityHeaders sends.
        app.MapRazorComponents<App>()
            .AddInteractiveWebAssemblyRenderMode()
            .AddAdditionalAssemblies(typeof(Routes).Assembly);

        return app;
    }

    /// <summary>
    /// Traefik forwards a WebSocket upgrade with <c>X-Forwarded-Proto: wss</c> (<c>ws</c> without TLS), which
    /// <c>UseForwardedHeaders</c> copies into the scheme. The client's page scheme is https (http): without this
    /// the HTTPS redirect answers the <c>/hubs/ui</c> upgrade with a 307 to itself, and the hub's Origin check
    /// compares the page's https Origin with wss.
    /// </summary>
    internal static Task UseHttpSchemeForWebSockets(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (string.Equals(request.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            request.Scheme = Uri.UriSchemeHttps;
        else if (string.Equals(request.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
            request.Scheme = Uri.UriSchemeHttp;
        return next(context);
    }
}
