namespace CodingAgent.Contracts.Auth;

/// <summary>
/// <c>GET /api/ui/me</c>: who the API sees and what the user token grants. Role names are the
/// configuration names (<see cref="AccessRoleNames"/>); "none" when there is no global role.
/// </summary>
public sealed record UiMeResponse(
    string? Subject,
    string? Username,
    string? IdentitySource,
    string GlobalRole,
    IReadOnlyList<UiMeProjectRole> ProjectRoles);

/// <summary>
/// A project role from the token. <see cref="ProjectName"/> is null and <see cref="Known"/> false
/// when no project has this ID; such a binding grants nothing usable.
/// </summary>
public sealed record UiMeProjectRole(string ProjectId, string? ProjectName, string Role, bool Known);
