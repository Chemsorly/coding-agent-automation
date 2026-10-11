namespace CodingAgent.Contracts.Auth;

/// <summary>
/// One binding that matched the principal. <see cref="ProjectId"/> is the bound project ID as
/// configured (normalised), or null for a global binding. Whether a project carries that ID is not
/// checked here; <c>GET /api/ui/me</c> answers that.
/// </summary>
public sealed record MatchedBinding(string Subject, AccessRole Role, string? ProjectId);

/// <summary>
/// The roles a principal holds: a global role plus per-project roles. The web host evaluates it
/// (<c>RbacEvaluator</c>) from the principal's claims and the configured bindings; the Pipeline API
/// reads it back from the user token (<see cref="AccessGrantClaims"/>).
/// Project IDs are normalised with <see cref="ProjectIds.Normalize(string?)"/>.
/// </summary>
public sealed record AccessGrant(
    AccessRole GlobalRole,
    IReadOnlyDictionary<string, AccessRole> ProjectRoles,
    IReadOnlyList<MatchedBinding> Matches)
{
    public static AccessGrant None { get; } = new(AccessRole.None, new Dictionary<string, AccessRole>(), []);

    /// <summary>
    /// The role on a project: the higher of the global role and the project's own role. An empty
    /// project ID means a global object (Req 6.2), which only the global role covers.
    /// </summary>
    public AccessRole RoleFor(string? projectId)
    {
        var id = ProjectIds.Normalize(projectId);
        if (id is null || !ProjectRoles.TryGetValue(id, out var projectRole))
            return GlobalRole;
        return projectRole > GlobalRole ? projectRole : GlobalRole;
    }

    /// <summary>No global role, but at least one project role: sees one project at a time.</summary>
    public bool IsScoped => GlobalRole == AccessRole.None && ProjectRoles.Count > 0;

    public bool HasAnyAccess => GlobalRole > AccessRole.None || ProjectRoles.Count > 0;

    public bool HasAnyOperator =>
        GlobalRole >= AccessRole.Operator || ProjectRoles.Values.Any(r => r >= AccessRole.Operator);
}

/// <summary>
/// Canonical form of project IDs so that <c>Guid?</c> sources (work items) and string sources
/// (runs, projects) compare equal: GUIDs in lower-case "D" format, other values trimmed, empty → null.
/// </summary>
public static class ProjectIds
{
    public static string? Normalize(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return null;
        return Guid.TryParse(projectId, out var guid) ? guid.ToString("D") : projectId.Trim();
    }

    /// <summary>The all-zero GUID is a real project ID (<c>WellKnownIds.DefaultProjectId</c>), not "no project".</summary>
    public static string? Normalize(Guid? projectId) =>
        projectId is { } guid ? guid.ToString("D") : null;
}
