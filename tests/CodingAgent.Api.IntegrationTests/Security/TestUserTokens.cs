using System.Security.Claims;
using CodingAgent.Contracts.Auth;
using CodingAgent.Infrastructure.Common.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// User tokens for the UI-surface tests, signed with <see cref="ApiWebApplicationFactory.UserTokenSigningKey"/>
/// unless a test wants a broken one.
/// </summary>
internal static class TestUserTokens
{
    public const string ProjectA = "6f1c2a9e-0000-0000-0000-00000000000a";
    public const string ProjectB = "6f1c2a9e-0000-0000-0000-00000000000b";

    private static SecurityKey Key => UserTokens.CreateSigningKey(ApiWebApplicationFactory.UserTokenSigningKey);

    public static AccessGrant Global(AccessRole role) => new(role, new Dictionary<string, AccessRole>(), []);

    public static AccessGrant Scoped(string projectId, AccessRole role) =>
        new(AccessRole.None, new Dictionary<string, AccessRole> { [projectId] = role }, []);

    public static string For(AccessGrant grant, string subject = "oidc:tester", string? username = "tester", string? identitySource = "oidc")
    {
        var now = DateTimeOffset.UtcNow;
        return UserTokens.Create(new UserTokenSubject(subject, username, identitySource), grant, now, now + UserTokens.HttpLifetime, Key);
    }

    /// <summary>A UI hub connect token (spec 051 D7): marked <c>use=hub</c>, valid for an hour.</summary>
    public static string Hub(AccessGrant grant)
    {
        var now = DateTimeOffset.UtcNow;
        return UserTokens.Create(new UserTokenSubject("oidc:tester", "tester", "oidc"), grant, now, now.AddHours(1), Key, UserTokenClaimTypes.HubUse);
    }

    public static string ValidUntil(AccessGrant grant, DateTimeOffset expires, string subject = "oidc:tester", string? username = "tester") =>
        UserTokens.Create(new UserTokenSubject(subject, username, "oidc"), grant, DateTimeOffset.UtcNow, expires, Key);

    public static string LocalAdmin() => For(Global(AccessRole.Admin), "local:admin", "admin", "local");

    public static string Expired(AccessGrant grant)
    {
        var issued = DateTimeOffset.UtcNow.AddMinutes(-10);
        return UserTokens.Create(new UserTokenSubject("oidc:tester", "tester", "oidc"), grant, issued, issued.AddMinutes(5), Key);
    }

    public static string SignedWithAnotherKey(AccessGrant grant)
    {
        var now = DateTimeOffset.UtcNow;
        return UserTokens.Create(new UserTokenSubject("oidc:tester", "tester", "oidc"), grant, now, now.AddMinutes(5),
            UserTokens.CreateSigningKey("not-the-api-signing-key-0123456789abcdef"));
    }

    public static string Unsigned(AccessGrant grant) => Raw(grant, UserTokens.Issuer, UserTokens.Audience, signingKey: null);

    public static string WithIssuer(string issuer, AccessGrant grant) => Raw(grant, issuer, UserTokens.Audience, Key);

    public static string WithAudience(string audience, AccessGrant grant) => Raw(grant, UserTokens.Issuer, audience, Key);

    private static string Raw(AccessGrant grant, string issuer, string audience, SecurityKey? signingKey)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new(UserTokenClaimTypes.Subject, "oidc:tester") };
        claims.AddRange(AccessGrantClaims.ToClaims(grant));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = signingKey is null ? null : new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
        });
    }
}
