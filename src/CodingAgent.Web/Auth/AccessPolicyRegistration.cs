using Microsoft.AspNetCore.Authorization;

namespace CodingAgent.Web.Auth;

/// <summary>Registers the page-level <see cref="AccessPolicies"/> (Spec 049 Req 7.1) for the cookie scheme.</summary>
public static class AccessPolicyRegistration
{
    internal static void Register(AuthorizationBuilder builder)
    {
        foreach (var (name, kind) in new[]
                 {
                     (AccessPolicies.AnyAccess, AccessPolicyKind.AnyAccess), (AccessPolicies.AnyOperator, AccessPolicyKind.AnyOperator),
                     (AccessPolicies.GlobalRead, AccessPolicyKind.GlobalRead), (AccessPolicies.Admin, AccessPolicyKind.Admin),
                 })
        {
            builder.AddPolicy(name, policy => policy
                .AddAuthenticationSchemes(UserAuthenticationRegistration.CookieScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new AccessRequirement(kind)));
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
