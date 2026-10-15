using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Evaluates the configured bindings against the principal's username and groups. A project
/// binding grants its role on the bound project ID as configured, without looking the project up
/// (spec 051 D2a): evaluation needs no API call, and an ID that no project carries is harmless
/// because no object has it. The profile page flags such IDs through <c>GET /api/ui/me</c>.
/// </summary>
public sealed class RbacEvaluator : IRbacEvaluator
{
    private readonly AccessRole _defaultRole;
    private readonly IReadOnlyList<Binding> _bindings;

    public RbacEvaluator(IOptions<AuthOptions> options)
    {
        var rbac = options.Value.Rbac;
        _defaultRole = AccessRoleNames.TryParse(rbac.DefaultRole, out var defaultRole) ? defaultRole : AccessRole.None;
        _bindings = rbac.Bindings.Select(Binding.From).OfType<Binding>().ToList();
    }

    public Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        Task.FromResult(Evaluate(user));

    private AccessGrant Evaluate(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return AccessGrant.None;

        if (AuthPrincipals.IsLocalAdmin(user))
            return new AccessGrant(AccessRole.Admin, new Dictionary<string, AccessRole>(), []);

        var username = AuthPrincipals.GetUsername(user);
        var groups = AuthPrincipals.GetGroups(user).ToHashSet(StringComparer.Ordinal);

        var globalRole = _defaultRole;
        var projectRoles = new Dictionary<string, AccessRole>(StringComparer.Ordinal);
        var matches = new List<MatchedBinding>();

        foreach (var binding in _bindings.Where(b => b.Matches(username, groups)))
        {
            matches.Add(new MatchedBinding(binding.Subject, binding.Role, binding.ProjectId));
            if (binding.ProjectId is null)
            {
                if (binding.Role > globalRole)
                    globalRole = binding.Role;
            }
            else if (!projectRoles.TryGetValue(binding.ProjectId, out var existing) || binding.Role > existing)
            {
                projectRoles[binding.ProjectId] = binding.Role;
            }
        }

        return new AccessGrant(globalRole, projectRoles, matches);
    }

    private sealed record Binding(string? Group, string? User, AccessRole Role, string? ProjectId)
    {
        public string Subject => Group is not null ? $"group:{Group}" : $"user:{User}";

        public bool Matches(string? username, HashSet<string> groups) =>
            Group is not null
                ? groups.Contains(Group)
                : username is not null && string.Equals(User, username, StringComparison.OrdinalIgnoreCase);

        /// <summary>Null for a binding the validator rejects; startup fails before evaluation then.</summary>
        public static Binding? From(RoleBindingOptions options)
        {
            if (!AccessRoleNames.TryParse(options.Role, out var role))
                return null;
            var group = string.IsNullOrWhiteSpace(options.Group) ? null : options.Group;
            var user = string.IsNullOrWhiteSpace(options.User) ? null : options.User.Trim();
            if ((group is null) == (user is null))
                return null;
            var projectId = string.IsNullOrWhiteSpace(options.ProjectId) ? null : ProjectIds.Normalize(options.ProjectId);
            if (role == AccessRole.Admin && projectId is not null)
                return null;
            return new Binding(group, user, role, projectId);
        }
    }
}
