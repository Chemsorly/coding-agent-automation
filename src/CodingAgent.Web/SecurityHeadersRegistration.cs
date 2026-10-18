using System.Security.Cryptography;
using CodingAgent.Web.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpOverrides;

namespace CodingAgent.Web;

/// <summary>
/// Browser hardening for every web response: an HTTPS redirect for clients that reached a
/// TLS-terminating ingress over plain HTTP, HSTS, and the security headers that browsers and an
/// OWASP ZAP baseline scan expect. Must run after <c>UseForwardedHeaders</c>, which supplies the
/// client's scheme.
/// </summary>
internal static class SecurityHeadersRegistration
{
    /// <summary>
    /// Redirect requests that a proxy received over plain HTTP. The chart sets it when the ingress
    /// has a TLS section, so the HTTPS URL exists.
    /// </summary>
    public const string HttpsRedirectKey = "WebUI:HttpsRedirect";

    /// <summary>
    /// The two bundles faro-init.js loads from the CDN (with SRI), allowed only when Faro is on. Exact URLs, not
    /// the CDN's origin, so an injected script tag cannot load any other package from it. Keep the version in
    /// step with FARO_VERSION in wwwroot/js/faro-init.js (ContentSecurityPolicyTests checks it).
    /// </summary>
    internal const string FaroScriptSources =
        "https://unpkg.com/@grafana/faro-web-sdk@2.8.1/dist/bundle/faro-web-sdk.iife.js "
        + "https://unpkg.com/@grafana/faro-web-tracing@2.8.1/dist/bundle/faro-web-tracing.iife.js";

    private const string CspNonceItem = "csp-nonce";

    internal const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    /// <summary>
    /// The one resource other sites may embed: dashboards (e.g. Homepage) show it as the service icon.
    /// It is public and served without sign-in; every other response stays same-origin.
    /// </summary>
    internal const string FaviconPath = "/favicon.svg";

    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>(HttpsRedirectKey))
            app.Use(RedirectProxiedHttpAsync);

        // HSTS only on HTTPS responses and never for localhost, so port-forwards keep working.
        app.UseHsts();
        var version = app.Services.GetRequiredService<BuildInfo>().ShortSha;
        var faroCollectorOrigin = FaroCollectorOrigin(app.Configuration["Faro:CollectorUrl"]);
        app.Use((context, next) =>
        {
            // Spec 052 Req 5.1: the build the web host runs, so an open client notices a new release.
            context.Response.Headers[AppVersionHeader] = version;
            return AddHeadersAsync(context, next, faroCollectorOrigin);
        });
        return app;
    }

    /// <summary>This request's Content-Security-Policy nonce, which the host page's inline scripts carry.</summary>
    public static string? GetCspNonce(this HttpContext context) => context.Items[CspNonceItem] as string;

    /// <summary>
    /// Spec 052 (ZAP baseline, Req 14.8): scripts only from the web host, the inline scripts of the host page
    /// with this response's nonce, WebAssembly compilation, and Faro's CDN when Faro is on. Fonts, images and
    /// calls stay on the web host, apart from the Faro collector. Styles allow 'unsafe-inline' because Blazor
    /// renders style attributes (an accepted exception, docs/internals/decisions.md). frame-ancestors is sent
    /// here instead of by Blazor, so a response carries one policy.
    /// </summary>
    internal static string ContentSecurityPolicy(string nonce, string? faroCollectorOrigin)
    {
        var faroScripts = faroCollectorOrigin is null ? "" : " " + FaroScriptSources;
        var faroCalls = faroCollectorOrigin is null ? "" : " " + faroCollectorOrigin;
        return "default-src 'self'; "
            + $"script-src 'self' 'nonce-{nonce}' 'wasm-unsafe-eval'{faroScripts}; "
            + "style-src 'self' 'unsafe-inline'; "
            + "img-src 'self'; font-src 'self'; "
            + $"connect-src 'self'{faroCalls}; "
            + "base-uri 'self'; form-action 'self'; frame-ancestors 'self'; object-src 'none'";
    }

    /// <summary>The origin (scheme, host, port) of the Faro collector, or null when Faro is off or the URL is not absolute.</summary>
    internal static string? FaroCollectorOrigin(string? collectorUrl) =>
        Uri.TryCreate(collectorUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    /// <summary>The web host's build version on every response (spec 052 Req 5.1).</summary>
    public const string AppVersionHeader = "X-App-Version";

    /// <summary>
    /// Sends a client that used plain HTTP to the ingress to the same URL over HTTPS, before it can
    /// post a password. Requests without a forwarded scheme (port-forward, probes, in-cluster
    /// calls) are served as they are.
    /// </summary>
    private static Task RedirectProxiedHttpAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var forwardedOverHttp = !request.IsHttps
            && request.Headers.ContainsKey(ForwardedHeadersDefaults.XOriginalProtoHeaderName);
        if (!forwardedOverHttp || !request.Host.HasValue)
            return next(context);

        // Without the port: the HTTP port of the ingress is not its HTTPS port.
        var url = UriHelper.BuildAbsolute("https", new HostString(request.Host.Host), request.PathBase, request.Path, request.QueryString);
        context.Response.Redirect(url, permanent: false, preserveMethod: true);
        return Task.CompletedTask;
    }

    private static Task AddHeadersAsync(HttpContext context, RequestDelegate next, string? faroCollectorOrigin)
    {
        // Hex, not Base64: a nonce with no "+", "/" or "=" needs no HTML encoding in the nonce attribute.
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        context.Items[CspNonceItem] = nonce;
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy(nonce, faroCollectorOrigin);
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = PermissionsPolicy;
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] =
            context.Request.Path.Equals(FaviconPath, StringComparison.OrdinalIgnoreCase) ? "cross-origin" : "same-origin";
        return next(context);
    }
}
