using System.Net;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Spec 052 3.4: the browser's handler on every UI client and the session client.
/// <list type="bullet">
/// <item>Adds <c>X-CSRF: 1</c> to every request, whatever the method (Req 2.6).</item>
/// <item>Reports the web host's <c>X-App-Version</c> to <see cref="VersionWatch"/> (Req 5.2).</item>
/// <item>On a 401 from a proxied call, checks the session first (Req 3.3). Only when the session is
/// gone does the browser go to the login page; otherwise the 401 came from the API (for example an
/// unknown route during a version skew) and the call fails like any other refusal. Navigating on
/// every 401 would loop between the login page and the page that keeps getting it.</item>
/// </list>
/// </summary>
public sealed class BffHttpHandler(VersionWatch version, IServiceProvider services) : DelegatingHandler
{
    public const string CsrfHeader = "X-CSRF";
    public const string AppVersionHeader = "X-App-Version";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(CsrfHeader, "1");
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues(AppVersionHeader, out var versions))
            version.Report(versions.FirstOrDefault());

        if (response.StatusCode == HttpStatusCode.Unauthorized && !IsSessionRequest(request))
            await services.GetRequiredService<SessionStore>().CheckNowAsync(cancellationToken);

        return response;
    }

    /// <summary>The session client handles its own 401 (<see cref="SessionStore"/>).</summary>
    private static bool IsSessionRequest(HttpRequestMessage request) =>
        request.RequestUri is { } uri
        && (uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString).EndsWith(SessionStore.Path, StringComparison.OrdinalIgnoreCase);
}
