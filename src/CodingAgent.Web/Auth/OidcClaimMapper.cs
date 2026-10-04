using System.Security.Claims;
using System.Text.Json;

namespace CodingAgent.Web.Auth;

/// <summary>Result of mapping a validated ID token to a session principal.</summary>
public sealed record OidcMappingResult(ClaimsPrincipal Principal, string Subject, string Username, int GroupCount, bool GroupOverage);

/// <summary>
/// Reduces the claims of a validated OIDC ID token to the session identity (Spec 049 Req 3.4–3.6):
/// subject, username, email, display name, groups, identity source and session expiry. Expects raw
/// claim types (the handler runs with <c>MapInboundClaims = false</c>).
/// </summary>
public static class OidcClaimMapper
{
    public static OidcMappingResult Map(ClaimsPrincipal token, OidcProviderOptions options, DateTimeOffset expiresAt)
    {
        var subject = First(token, "sub") ?? "";
        var email = First(token, "email");
        var username = First(token, options.UsernameClaim) ?? email ?? subject;
        var groups = ReadGroups(token, options.GroupsClaim);
        var overage = groups.Count == 0 && HasGroupOverage(token, options.GroupsClaim);

        var principal = AuthPrincipals.Create(
            IdentitySources.Oidc, subject, username, email, First(token, "name"), groups, expiresAt);
        return new OidcMappingResult(principal, subject, username, groups.Count, overage);
    }

    private static string? First(ClaimsPrincipal token, string type)
    {
        var value = token.FindFirst(type)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Accepts one claim per group (how JSON arrays arrive), a single string, or a claim whose value
    /// is a serialized JSON array.
    /// </summary>
    private static List<string> ReadGroups(ClaimsPrincipal token, string claimType)
    {
        var groups = new List<string>();
        foreach (var claim in token.FindAll(claimType))
        {
            var value = claim.Value.Trim();
            if (value.StartsWith('[') && TryReadJsonArray(value, groups))
                continue;
            if (value.Length > 0)
                groups.Add(value);
        }
        return groups.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool TryReadJsonArray(string value, List<string> groups)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            groups.AddRange(document.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(g => g.Length > 0));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Entra ID sends no groups claim when a user has more than ~200 groups; the token then names
    /// the claim in <c>_claim_names</c> or carries <c>hasgroups: true</c>.
    /// </summary>
    private static bool HasGroupOverage(ClaimsPrincipal token, string groupsClaim)
    {
        if (string.Equals(First(token, "hasgroups"), "true", StringComparison.OrdinalIgnoreCase))
            return true;

        var claimNames = First(token, "_claim_names");
        if (claimNames is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(claimNames);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(groupsClaim, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
