using System.Security.Claims;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Web.Auth;

/// <summary>The key the web host signs user tokens with (spec 051 D2). Only the Pipeline API shares it.</summary>
internal sealed record UserTokenSigningKey(SecurityKey Key);

/// <summary>
/// Spec 051 D2: the user token for the Pipeline API's UI surface, for one DI scope (a Blazor circuit
/// or a prerender request). It is minted from the scope's signed-in user and their evaluated grant.
/// It lasts <see cref="UserTokens.HttpLifetime"/>, never past the session, and is minted again
/// shortly before it expires or when the user changes. A scope only ever hands out its own user's
/// token. There is none for an anonymous user or a user without any role.
/// The UI hub's token is minted the same way but lasts the whole session (D7).
/// </summary>
internal sealed class UserTokenProvider(
    AuthenticationStateProvider authenticationState,
    IRbacEvaluator rbac,
    UserTokenSigningKey signingKey,
    TimeProvider time,
    ILogger<UserTokenProvider> logger) : IUiAccessTokenSource
{
    /// <summary>A token this close to expiry is replaced, so it does not run out on the way to the API.</summary>
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private Minted _http;
    private Minted _hub;

    public async ValueTask<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        _http = await GetAsync(_http, now, _ => now + UserTokens.HttpLifetime, ct);
        return _http.Token;
    }

    public async ValueTask<string?> GetHubTokenAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        _hub = await GetAsync(_hub, now, user => AuthPrincipals.GetSessionExpiry(user) ?? now + UserTokens.HttpLifetime, ct);
        return _hub.Token;
    }

    /// <summary>The <paramref name="current"/> token while it is the same user's and not about to expire, else a new one.</summary>
    private async Task<Minted> GetAsync(Minted current, DateTimeOffset now, Func<ClaimsPrincipal, DateTimeOffset> lifetimeEnd, CancellationToken ct)
    {
        var user = (await authenticationState.GetAuthenticationStateAsync()).User;
        if (current.Token is not null && ReferenceEquals(user, current.User) && now < current.Expires - RenewBefore)
            return current;

        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst(AuthClaimTypes.Subject)?.Value : null;
        if (string.IsNullOrEmpty(subject))
            return new Minted(user, null, default);

        var grant = await rbac.EvaluateAsync(user, ct);
        if (!grant.HasAnyAccess)
            return new Minted(user, null, default);

        var expires = lifetimeEnd(user);
        if (AuthPrincipals.GetSessionExpiry(user) is { } sessionExpires && sessionExpires < expires)
            expires = sessionExpires;
        if (expires <= now)
            return new Minted(user, null, default);

        var tokenSubject = new UserTokenSubject(
            subject, AuthPrincipals.GetUsername(user), user.FindFirst(AuthClaimTypes.IdentitySource)?.Value);
        var token = UserTokens.Create(tokenSubject, grant, now, expires, signingKey.Key);
        logger.LogDebug("Minted a user token valid until {Expires:O}", expires);
        return new Minted(user, token, expires);
    }

    private readonly record struct Minted(ClaimsPrincipal? User, string? Token, DateTimeOffset Expires);
}
