using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Re-checks the session of a live Blazor circuit every 5 minutes (Req 4.3). A circuit can outlive
/// its cookie; once the session's expiry has passed, the circuit's user becomes anonymous and the
/// router sends the user to the login page on the next render.
/// </summary>
internal sealed class SessionRevalidatingAuthenticationStateProvider(ILoggerFactory loggerFactory, TimeProvider time)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(IsSessionValid(authenticationState, time.GetUtcNow()));

    internal static bool IsSessionValid(AuthenticationState state, DateTimeOffset now) =>
        AuthPrincipals.GetSessionExpiry(state.User) is { } expires && expires > now;
}
