using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Spec 052 Req 3.3: sends the browser to the login page with a full page load, so the static login
/// page renders on the web host and the cookie handler sees the request. The current page is the
/// return URL. Only the first call navigates.
/// </summary>
public sealed class LoginNavigator(NavigationManager nav)
{
    private int _navigating;

    public bool IsNavigating => Volatile.Read(ref _navigating) == 1;

    public void Go()
    {
        if (Interlocked.Exchange(ref _navigating, 1) == 1)
            return;

        // Sign-in requires the UI at the root of its host, so the path is the app-relative URL.
        var returnUrl = new Uri(nav.Uri).PathAndQuery;
        nav.NavigateTo($"{AuthPaths.Login.TrimStart('/')}?{AuthPaths.ReturnUrlParameter}={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
    }
}
