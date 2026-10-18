using Microsoft.AspNetCore.Authorization;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Registers the page-level <see cref="AccessPolicies"/> (Spec 049 Req 7.1). The web host registers
/// them for its cookie scheme, so a full page load is checked there (spec 052 Req 1.6); the browser
/// registers them without a scheme for in-app navigation.
/// </summary>
public static class AccessPolicyRegistration
{
    public static void Register(AuthorizationOptions options, string? scheme)
    {
        foreach (var (name, kind) in new[]
                 {
                     (AccessPolicies.AnyAccess, AccessPolicyKind.AnyAccess), (AccessPolicies.AnyOperator, AccessPolicyKind.AnyOperator),
                     (AccessPolicies.GlobalRead, AccessPolicyKind.GlobalRead), (AccessPolicies.Admin, AccessPolicyKind.Admin),
                 })
        {
            options.AddPolicy(name, policy =>
            {
                if (scheme is not null)
                    policy.AddAuthenticationSchemes(scheme);
                policy.RequireAuthenticatedUser().AddRequirements(new AccessRequirement(kind));
            });
        }
    }
}

public sealed record AccessRequirement(AccessPolicyKind Kind) : IAuthorizationRequirement;

/// <summary>Evaluates <see cref="AccessRequirement"/> from the principal's bindings.</summary>
public sealed class AccessAuthorizationHandler(IRbacEvaluator rbac) : AuthorizationHandler<AccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
    {
        var grant = await rbac.EvaluateAsync(context.User);
        if (AccessPolicyRules.IsSatisfied(grant, requirement.Kind))
            context.Succeed(requirement);
    }
}
