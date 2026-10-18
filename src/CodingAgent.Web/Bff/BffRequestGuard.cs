using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace CodingAgent.Web.Bff;

/// <summary>
/// Spec 052 DD7: refuses requests to <c>/api/*</c>, <c>/hubs/*</c> and <c>/bff/*</c> before
/// authentication, so a refused request is never forwarded (Req 2.1, 2.6, 2.7). The rules apply in
/// this order (Req 2.11):
/// <list type="number">
/// <item>An encoded slash or backslash (<c>%2F</c>, <c>%5C</c>) in the raw path: 400. Kestrel already
/// resolves <c>.</c>, <c>..</c> and <c>%2e</c> before routing, so these are the only way to leave a
/// proxied prefix. This also refuses any ID that contains <c>/</c>; that is safe because the
/// providers emit plain issue numbers and UI-created IDs are GUIDs. A config-imported ID with a
/// <c>/</c> cannot be used through the UI.</item>
/// <item><c>/hubs/ui</c> without a WebSocket upgrade: 400.</item>
/// <item>A <c>/hubs/ui</c> upgrade whose <c>Origin</c> is missing or not the own origin: 403.</item>
/// <item>Any other request, whatever the method, without <c>X-CSRF: 1</c>, with a foreign
/// <c>Origin</c> or with <c>Sec-Fetch-Site: cross-site</c>: 400. Some GETs have effects
/// (<c>GET /api/ui/settings/models</c> starts a Job), so GET is checked too.</item>
/// </list>
/// Without CORS, a cross-site page can neither set the header nor read the answer.
/// </summary>
internal static class BffRequestGuard
{
    public const string CsrfHeader = "X-CSRF";
    public const string CsrfHeaderValue = "1";

    public static bool AppliesTo(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/bff");

    public static Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (Refusal(context) is not { } status)
            return next(context);

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BffRequestGuard));
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Refused {Method} {Path} with {Status}",
                LogSanitizer.SanitizeForLog(context.Request.Method), LogSanitizer.SanitizeForLog(context.Request.Path.Value), status);
        }
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    /// <summary>The status code that refuses the request, or null when it may proceed.</summary>
    internal static int? Refusal(HttpContext context)
    {
        var request = context.Request;
        var rawPath = (context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? request.Path.Value ?? "").Split('?', 2)[0];
        if (rawPath.Contains("%2F", StringComparison.OrdinalIgnoreCase) || rawPath.Contains("%5C", StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status400BadRequest;

        if (request.Path.Equals(HubRoutes.Ui, StringComparison.OrdinalIgnoreCase))
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return StatusCodes.Status400BadRequest;
            return IsOwnOrigin(request, request.Headers.Origin) ? null : StatusCodes.Status403Forbidden;
        }

        if (request.Headers[CsrfHeader] != CsrfHeaderValue)
            return StatusCodes.Status400BadRequest;
        if (!StringValues.IsNullOrEmpty(request.Headers.Origin) && !IsOwnOrigin(request, request.Headers.Origin))
            return StatusCodes.Status400BadRequest;
        if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status400BadRequest;
        return null;
    }

    /// <summary>
    /// Whether <paramref name="origin"/> is this host's public origin: the scheme after
    /// <c>UseForwardedHeaders</c> and the <c>Host</c> header, compared case-insensitively, with the
    /// default ports (80, 443) dropped on both sides.
    /// </summary>
    internal static bool IsOwnOrigin(HttpRequest request, StringValues origin)
    {
        if (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var parsed) || !request.Host.HasValue)
            return false;

        var ownPort = request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return string.Equals(parsed.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsed.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && parsed.Port == ownPort
            && parsed.AbsolutePath == "/";
    }
}
