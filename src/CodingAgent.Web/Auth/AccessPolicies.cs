using Microsoft.AspNetCore.Authorization;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Page-level authorization policies (Spec 049 Req 7.1). Project-level decisions never go through
/// policies: components use <see cref="CurrentAccess"/> to render and <see cref="IAccessGuard"/> to
/// enforce mutations.
/// </summary>
public static class AccessPolicies
{
    /// <summary>A global role or a role on at least one project: the cockpit pages.</summary>
    public const string AnyAccess = "Access.Any";

    /// <summary>Global operator, or operator on at least one project: Agent Chat.</summary>
    public const string AnyOperator = "Access.AnyOperator";

    /// <summary>Global readonly or higher: Fleet and Consolidation.</summary>
    public const string GlobalRead = "Access.GlobalRead";

    /// <summary>Global admin: Settings.</summary>
    public const string Admin = "Access.Admin";

    internal static void Register(AuthorizationBuilder builder)
    {
        foreach (var (name, kind) in new[]
                 {
                     (AnyAccess, AccessPolicyKind.AnyAccess), (AnyOperator, AccessPolicyKind.AnyOperator),
                     (GlobalRead, AccessPolicyKind.GlobalRead), (Admin, AccessPolicyKind.Admin),
                 })
        {
            builder.AddPolicy(name, policy => policy
                .AddAuthenticationSchemes(UserAuthenticationRegistration.CookieScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new AccessRequirement(kind)));
        }
    }
}

public enum AccessPolicyKind
{
    AnyAccess,
    AnyOperator,
    GlobalRead,
    Admin,
}

public sealed record AccessRequirement(AccessPolicyKind Kind) : IAuthorizationRequirement;

/// <summary>Evaluates <see cref="AccessRequirement"/> from the principal's bindings.</summary>
public sealed class AccessAuthorizationHandler(IRbacEvaluator rbac) : AuthorizationHandler<AccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
    {
        var grant = await rbac.EvaluateAsync(context.User);
        if (IsSatisfied(grant, requirement.Kind))
            context.Succeed(requirement);
    }

    internal static bool IsSatisfied(AccessGrant grant, AccessPolicyKind kind) => kind switch
    {
        AccessPolicyKind.AnyAccess => grant.HasAnyAccess,
        AccessPolicyKind.AnyOperator => grant.HasAnyOperator,
        AccessPolicyKind.GlobalRead => grant.GlobalRole >= AccessRole.ReadOnly,
        AccessPolicyKind.Admin => grant.GlobalRole == AccessRole.Admin,
        _ => false,
    };
}
