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

    // No script-src or style-src: App.razor has inline scripts and Blazor applies inline styles.
    // frame-ancestors is sent here instead of by Blazor, so a response carries one policy.
    internal const string ContentSecurityPolicy =
        "base-uri 'self'; form-action 'self'; frame-ancestors 'self'; object-src 'none'";

    internal const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>(HttpsRedirectKey))
            app.Use(RedirectProxiedHttpAsync);

        // HSTS only on HTTPS responses and never for localhost, so port-forwards keep working.
        app.UseHsts();
        app.Use(AddHeadersAsync);
        return app;
    }

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

    private static Task AddHeadersAsync(HttpContext context, RequestDelegate next)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = PermissionsPolicy;
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return next(context);
    }
}
