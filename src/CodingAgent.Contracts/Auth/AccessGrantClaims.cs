using System.Security.Claims;

namespace CodingAgent.Contracts.Auth;

/// <summary>Claim types of the user token the web host signs for the Pipeline API (spec 051).</summary>
public static class UserTokenClaimTypes
{
    public const string Subject = "sub";
    public const string Username = "username";

    /// <summary>"local" or "oidc".</summary>
    public const string IdentitySource = "idp";

    /// <summary>The global role name, absent when the user has none.</summary>
    public const string GlobalRole = "caa_role";

    /// <summary>One claim per project role, "{projectId}:{role}".</summary>
    public const string ProjectRole = "caa_prj";

    /// <summary>
    /// What the token may be used for: <see cref="HubUse"/> marks the session-long UI hub token (spec 051 D7),
    /// which the HTTP UI surface refuses. HTTP tokens carry none.
    /// </summary>
    public const string Use = "use";

    /// <summary>The <see cref="Use"/> value of a UI hub connect token.</summary>
    public const string HubUse = "hub";
}

/// <summary>
/// Writes an <see cref="AccessGrant"/> into token claims and reads it back. The bindings that
/// produced the grant (<see cref="AccessGrant.Matches"/>) are not carried. A project-scoped admin
/// (#3577) is carried like the other project roles; it covers nothing global.
/// </summary>
public static class AccessGrantClaims
{
    public static IEnumerable<Claim> ToClaims(AccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Enumerate(grant);

        static IEnumerable<Claim> Enumerate(AccessGrant grant)
        {
            if (grant.GlobalRole > AccessRole.None)
                yield return new Claim(UserTokenClaimTypes.GlobalRole, AccessRoleNames.ToName(grant.GlobalRole));

            foreach (var (projectId, role) in grant.ProjectRoles.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (role > AccessRole.None)
                    yield return new Claim(UserTokenClaimTypes.ProjectRole, $"{projectId}:{AccessRoleNames.ToName(role)}");
            }
        }
    }

    public static AccessGrant FromClaims(IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var globalRole = AccessRole.None;
        var projectRoles = new Dictionary<string, AccessRole>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            if (claim.Type == UserTokenClaimTypes.GlobalRole)
            {
                if (AccessRoleNames.TryParse(claim.Value, out var role) && role > globalRole)
                    globalRole = role;
            }
            else if (claim.Type == UserTokenClaimTypes.ProjectRole && TryParseProjectRole(claim.Value, out var projectId, out var role)
                     && (!projectRoles.TryGetValue(projectId, out var existing) || role > existing))
            {
                projectRoles[projectId] = role;
            }
        }

        return new AccessGrant(globalRole, projectRoles, []);
    }

    private static bool TryParseProjectRole(string value, out string projectId, out AccessRole role)
    {
        projectId = "";
        role = AccessRole.None;
        var separator = value.LastIndexOf(':');
        if (separator <= 0)
            return false;

        var id = ProjectIds.Normalize(value[..separator]);
        if (id is null || !AccessRoleNames.TryParse(value[(separator + 1)..], out role))
            return false;

        projectId = id;
        return true;
    }
}
