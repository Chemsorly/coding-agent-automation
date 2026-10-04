using System.Runtime.CompilerServices;
using CodingAgent.Infrastructure.Common;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Server-side permission check immediately before a mutation (Spec 049 Req 7.3). Not rendering a
/// control already blocks its handler in Blazor Server; the guard covers stale renders and values
/// that arrive from the browser.
/// </summary>
public interface IAccessGuard
{
    /// <summary>
    /// Throws <see cref="AccessDeniedException"/> unless the signed-in principal holds at least
    /// <paramref name="minRole"/> on <paramref name="projectId"/> (empty = a global object).
    /// </summary>
    Task DemandAsync(AccessRole minRole, string? projectId = null, [CallerMemberName] string action = "");
}

/// <summary>Thrown by <see cref="IAccessGuard"/>; callers show <see cref="UserMessage"/>.</summary>
public sealed class AccessDeniedException(string action)
    : Exception($"Access denied for '{action}'.")
{
    public const string UserMessage = "You don't have permission to do this";

    public string Action { get; } = action;
}

/// <summary>Re-evaluates the circuit principal's grant on every check, so it never acts on a stale render.</summary>
public sealed class AccessGuard(CurrentAccess access, IRbacEvaluator rbac, ILogger<AccessGuard> logger) : IAccessGuard
{
    public async Task DemandAsync(AccessRole minRole, string? projectId = null, [CallerMemberName] string action = "")
    {
        await access.InitializeAsync();
        var grant = await rbac.EvaluateAsync(access.User);
        if (grant.RoleFor(projectId) >= minRole)
            return;

        logger.LogWarning(
            "Access denied: user '{Username}' needs {Role} on project '{ProjectId}' for {Action}",
            LogSanitizer.SanitizeForLog(access.Username),
            AccessRoleNames.ToName(minRole),
            LogSanitizer.SanitizeForLog(projectId),
            action);
        throw new AccessDeniedException(action);
    }
}
