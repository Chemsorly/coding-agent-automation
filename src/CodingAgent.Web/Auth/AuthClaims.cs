using System.Globalization;
using System.Security.Claims;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Claim types of the session cookie. The cookie carries identity only (Req 3.4); roles are
/// evaluated from these claims by <see cref="IRbacEvaluator"/>.
/// </summary>
public static class AuthClaimTypes
{
    public const string Subject = "sub";
    public const string Username = "username";
    public const string Email = "email";
    public const string DisplayName = "display_name";

    /// <summary>One claim per group value.</summary>
    public const string Group = "group";

    /// <summary><see cref="IdentitySources.Local"/> or <see cref="IdentitySources.Oidc"/>.</summary>
    public const string IdentitySource = "idp";

    /// <summary>Session expiry as ISO 8601 UTC ("O" format).</summary>
    public const string SessionExpires = "session_expires";
}

public static class IdentitySources
{
    public const string Local = "local";
    public const string Oidc = "oidc";
}

/// <summary>Builds and reads session principals.</summary>
public static class AuthPrincipals
{
    public const string LocalAdminSubject = "local:admin";

    /// <summary>Authentication type of every session identity (also the cookie scheme name).</summary>
    public const string AuthenticationType = "Cookies";

    public static ClaimsPrincipal CreateLocalAdmin(DateTimeOffset expiresAt) =>
        Create(IdentitySources.Local, LocalAdminSubject, LocalAdminOptions.Username, email: null,
            displayName: LocalAdminOptions.Username, groups: [], expiresAt);

    public static ClaimsPrincipal Create(
        string identitySource,
        string subject,
        string username,
        string? email,
        string? displayName,
        IEnumerable<string> groups,
        DateTimeOffset expiresAt)
    {
        var claims = new List<Claim>
        {
            new(AuthClaimTypes.Subject, subject),
            new(AuthClaimTypes.Username, username),
            new(AuthClaimTypes.IdentitySource, identitySource),
            new(AuthClaimTypes.SessionExpires, expiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrEmpty(email))
            claims.Add(new Claim(AuthClaimTypes.Email, email));
        if (!string.IsNullOrEmpty(displayName))
            claims.Add(new Claim(AuthClaimTypes.DisplayName, displayName));
        claims.AddRange(groups.Distinct(StringComparer.Ordinal).Select(g => new Claim(AuthClaimTypes.Group, g)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, AuthClaimTypes.Username, roleType: null));
    }

    public static bool IsLocalAdmin(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirst(AuthClaimTypes.IdentitySource)?.Value == IdentitySources.Local
        && user.FindFirst(AuthClaimTypes.Subject)?.Value == LocalAdminSubject;

    public static string? GetUsername(ClaimsPrincipal user) => user.FindFirst(AuthClaimTypes.Username)?.Value;

    public static IReadOnlyList<string> GetGroups(ClaimsPrincipal user) =>
        user.FindAll(AuthClaimTypes.Group).Select(c => c.Value).ToList();

    public static DateTimeOffset? GetSessionExpiry(ClaimsPrincipal user) =>
        DateTimeOffset.TryParse(user.FindFirst(AuthClaimTypes.SessionExpires)?.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)
            ? expires
            : null;
}
