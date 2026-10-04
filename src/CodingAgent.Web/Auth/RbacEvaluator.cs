using System.Collections.Concurrent;
using System.Security.Claims;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.Auth;

/// <summary>Maps an authenticated principal to its roles (Spec 049 Req 5).</summary>
public interface IRbacEvaluator
{
    Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>
/// Evaluates the configured bindings against the principal's username and groups. Project names
/// are resolved through <see cref="IProjectStore"/> (cached by <c>ApiProjectStore</c>), so a
/// project created in Settings is picked up without a restart. A project binding whose name is
/// unknown or not unique grants nothing; if the project list cannot be loaded, project bindings
/// grant nothing and global bindings keep working (fail closed).
/// </summary>
public sealed class RbacEvaluator : IRbacEvaluator
{
    internal const string UnknownProject = "unknown project";
    internal const string DuplicateProject = "duplicate project name";
    internal const string ProjectsUnavailable = "project list unavailable";

    private readonly IProjectStore _projects;
    private readonly ILogger<RbacEvaluator> _logger;
    private readonly AccessRole _defaultRole;
    private readonly IReadOnlyList<Binding> _bindings;
    private readonly ConcurrentDictionary<string, byte> _reportedProblems = new(StringComparer.Ordinal);

    public RbacEvaluator(IOptions<AuthOptions> options, IProjectStore projects, ILogger<RbacEvaluator> logger)
    {
        _projects = projects;
        _logger = logger;
        var rbac = options.Value.Rbac;
        _defaultRole = AccessRoleNames.TryParse(rbac.DefaultRole, out var defaultRole) ? defaultRole : AccessRole.None;
        _bindings = rbac.Bindings.Select(Binding.From).OfType<Binding>().ToList();
    }

    public async Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.Identity?.IsAuthenticated != true)
            return AccessGrant.None;

        if (AuthPrincipals.IsLocalAdmin(user))
            return new AccessGrant(AccessRole.Admin, new Dictionary<string, AccessRole>(), []);

        var username = AuthPrincipals.GetUsername(user);
        var groups = AuthPrincipals.GetGroups(user).ToHashSet(StringComparer.Ordinal);
        var matched = _bindings.Where(b => b.Matches(username, groups)).ToList();

        var globalRole = _defaultRole;
        var projectRoles = new Dictionary<string, AccessRole>(StringComparer.Ordinal);
        var matches = new List<MatchedBinding>();

        foreach (var binding in matched.Where(b => b.ProjectName is null))
        {
            if (binding.Role > globalRole)
                globalRole = binding.Role;
            matches.Add(new MatchedBinding(binding.Subject, binding.Role, null, null, null));
        }

        var projectBindings = matched.Where(b => b.ProjectName is not null).ToList();
        if (projectBindings.Count > 0)
        {
            var projects = await TryLoadProjectsAsync(ct);
            foreach (var binding in projectBindings)
            {
                var (projectId, problem) = Resolve(binding.ProjectName!, projects);
                matches.Add(new MatchedBinding(binding.Subject, binding.Role, binding.ProjectName, projectId, problem));
                if (problem is not null)
                {
                    ReportOnce(binding.ProjectName!, problem);
                    continue;
                }

                if (!projectRoles.TryGetValue(projectId!, out var existing) || binding.Role > existing)
                    projectRoles[projectId!] = binding.Role;
            }
        }

        return new AccessGrant(globalRole, projectRoles, matches);
    }

    private async Task<IReadOnlyList<PipelineProject>?> TryLoadProjectsAsync(CancellationToken ct)
    {
        try
        {
            return await _projects.LoadProjectsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RBAC: could not load the project list; project-scoped bindings grant nothing until it loads");
            return null;
        }
    }

    private static (string? ProjectId, string? Problem) Resolve(string projectName, IReadOnlyList<PipelineProject>? projects)
    {
        if (projects is null)
            return (null, ProjectsUnavailable);

        var found = projects.Where(p => string.Equals(p.Name, projectName, StringComparison.Ordinal)).Take(2).ToList();
        return found.Count switch
        {
            0 => (null, UnknownProject),
            1 => (ProjectIds.Normalize(found[0].Id), null),
            _ => (null, DuplicateProject),
        };
    }

    private void ReportOnce(string projectName, string problem)
    {
        if (problem == ProjectsUnavailable || !_reportedProblems.TryAdd($"{problem}\n{projectName}", 0))
            return;
        _logger.LogWarning("RBAC: binding for project '{ProjectName}' grants nothing ({Problem})",
            LogSanitizer.SanitizeForLog(projectName), problem);
    }

    private sealed record Binding(string? Group, string? User, AccessRole Role, string? ProjectName)
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
            var project = string.IsNullOrWhiteSpace(options.Project) ? null : options.Project;
            if (role == AccessRole.Admin && project is not null)
                return null;
            return new Binding(group, user, role, project);
        }
    }
}
