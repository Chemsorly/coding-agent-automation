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

    private ClaimsPrincipal? _user;
    private string? _token;
    private DateTimeOffset _expires;

    public async ValueTask<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var user = (await authenticationState.GetAuthenticationStateAsync()).User;
        var now = time.GetUtcNow();
        if (_token is not null && ReferenceEquals(user, _user) && now < _expires - RenewBefore)
            return _token;

        _user = user;
        _token = null;
        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst(AuthClaimTypes.Subject)?.Value : null;
        if (string.IsNullOrEmpty(subject))
            return null;

        var grant = await rbac.EvaluateAsync(user, ct);
        if (!grant.HasAnyAccess)
            return null;

        var expires = now + UserTokens.HttpLifetime;
        if (AuthPrincipals.GetSessionExpiry(user) is { } sessionExpires && sessionExpires < expires)
            expires = sessionExpires;
        if (expires <= now)
            return null;

        var tokenSubject = new UserTokenSubject(
            subject, AuthPrincipals.GetUsername(user), user.FindFirst(AuthClaimTypes.IdentitySource)?.Value);
        _token = UserTokens.Create(tokenSubject, grant, now, expires, signingKey.Key);
        _expires = expires;
        logger.LogDebug("Minted a user token valid until {Expires:O}", expires);
        return _token;
    }
}
