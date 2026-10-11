using System.Security.Claims;
using System.Text;
using CodingAgent.Contracts.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Infrastructure.Common.Auth;

/// <summary>Who a user token is for. <see cref="Subject"/> is the stable ID; the others are informational.</summary>
public sealed record UserTokenSubject(string Subject, string? Username, string? IdentitySource);

/// <summary>
/// The user token (spec 051 D2): a short-lived HS256 JWT the web host signs for the signed-in user
/// and the Pipeline API validates on its UI surface (<c>/api/ui/**</c>). It carries the user's
/// <see cref="AccessGrant"/>; the API enforces what the token says. The signing key is shared by
/// those two hosts only.
/// </summary>
public static class UserTokens
{
    public const string AuthenticationScheme = "User";
    public const string Issuer = "caa-web";
    public const string Audience = "caa-api";

    /// <summary>IConfiguration key of the signing key (Helm: <c>UserToken__SigningKey</c>).</summary>
    public const string SigningKeyConfigKey = "UserToken:SigningKey";

    /// <summary>Minimum key length in characters: HS256 needs at least 256 bits.</summary>
    public const int MinimumKeyLength = 32;

    /// <summary>Lifetime of tokens for HTTP calls.</summary>
    public static readonly TimeSpan HttpLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds the signing key from its configured value. Throws when the value is missing or shorter
    /// than <see cref="MinimumKeyLength"/>, so a host never starts with a guessable key.
    /// </summary>
    public static SymmetricSecurityKey CreateSigningKey(string? configuredKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException($"{SigningKeyConfigKey} is not configured.");
        if (configuredKey.Length < MinimumKeyLength)
            throw new InvalidOperationException($"{SigningKeyConfigKey} must be at least {MinimumKeyLength} characters long.");

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuredKey));
    }

    /// <summary>Signs a token for <paramref name="subject"/> with the claims of <paramref name="grant"/>.</summary>
    public static string Create(
        UserTokenSubject subject, AccessGrant grant, DateTimeOffset issuedAt, DateTimeOffset expires, SecurityKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (expires <= issuedAt)
            throw new ArgumentOutOfRangeException(nameof(expires), "A user token must expire after it is issued.");

        var claims = new List<Claim> { new(UserTokenClaimTypes.Subject, subject.Subject) };
        if (!string.IsNullOrEmpty(subject.Username))
            claims.Add(new Claim(UserTokenClaimTypes.Username, subject.Username));
        if (!string.IsNullOrEmpty(subject.IdentitySource))
            claims.Add(new Claim(UserTokenClaimTypes.IdentitySource, subject.IdentitySource));
        claims.AddRange(AccessGrantClaims.ToClaims(grant));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Validation rules for the API: HS256 only, issuer, audience, lifetime and signature.</summary>
    public static TokenValidationParameters CreateValidationParameters(SecurityKey signingKey) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = signingKey,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = ClockSkew,
        NameClaimType = UserTokenClaimTypes.Username,
    };
}
