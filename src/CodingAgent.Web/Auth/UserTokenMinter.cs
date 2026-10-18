using System.Security.Claims;
using CodingAgent.Infrastructure.Common.Auth;

namespace CodingAgent.Web.Auth;

/// <summary>The key the web host signs user tokens with (spec 051 D2). Only the Pipeline API shares it.</summary>
internal sealed record UserTokenSigningKey(Microsoft.IdentityModel.Tokens.SecurityKey Key);

/// <summary>A minted user token and the moment it stops being valid.</summary>
internal sealed record MintedUserToken(string Value, DateTimeOffset Expires);

/// <summary>
/// Spec 051 D2, spec 052 DD6: mints the user token for the Pipeline API's UI surface from a signed-in
/// principal and its evaluated grant. An HTTP token lasts <see cref="UserTokens.HttpLifetime"/>; the
/// UI hub's token lasts the whole session (051 D7) and is marked <c>use=hub</c>, so the API accepts it
/// only on the hub. Neither outlives the session. There is none for an anonymous user, a user
/// without any role, or an ended session.
/// </summary>
internal sealed class UserTokenMinter(
    IRbacEvaluator rbac,
    UserTokenSigningKey signingKey,
    TimeProvider time,
    ILogger<UserTokenMinter> logger)
{
    public async Task<MintedUserToken?> MintAsync(ClaimsPrincipal user, bool hub, CancellationToken ct = default)
    {
        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst(AuthClaimTypes.Subject)?.Value : null;
        if (string.IsNullOrEmpty(subject))
            return null;

        var grant = await rbac.EvaluateAsync(user, ct);
        if (!grant.HasAnyAccess)
            return null;

        var now = time.GetUtcNow();
        var sessionExpires = AuthPrincipals.GetSessionExpiry(user);
        var expires = hub ? sessionExpires ?? now + UserTokens.HttpLifetime : now + UserTokens.HttpLifetime;
        if (sessionExpires is { } end && end < expires)
            expires = end;
        if (expires <= now)
            return null;

        var tokenSubject = new UserTokenSubject(
            subject, AuthPrincipals.GetUsername(user), user.FindFirst(AuthClaimTypes.IdentitySource)?.Value);
        var token = UserTokens.Create(tokenSubject, grant, now, expires, signingKey.Key, hub ? UserTokenClaimTypes.HubUse : null);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Minted a user token valid until {Expires:O}", expires);
        return new MintedUserToken(token, expires);
    }
}
