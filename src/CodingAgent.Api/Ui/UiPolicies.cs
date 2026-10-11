using CodingAgent.Contracts.Auth;
using CodingAgent.Infrastructure.Common.Auth;
using Microsoft.AspNetCore.Authorization;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Authorization policies of the UI surface (<c>/api/ui/**</c>, spec 051 D3). Each accepts only the
/// user token, never an API key, and checks the grant the token carries. Project-level checks are
/// not policies: handlers call <see cref="IUserAccess"/> with the project of the stored object.
/// </summary>
public static class UiPolicies
{
    public const string AnyAccess = "Ui.AnyAccess";
    public const string AnyOperator = "Ui.AnyOperator";
    public const string GlobalRead = "Ui.GlobalRead";
    public const string GlobalOperate = "Ui.GlobalOperate";
    public const string Admin = "Ui.Admin";

    /// <summary>Every UI policy with the requirement it checks.</summary>
    public static IReadOnlyDictionary<string, AccessPolicyKind> All { get; } = new Dictionary<string, AccessPolicyKind>
    {
        [AnyAccess] = AccessPolicyKind.AnyAccess,
        [AnyOperator] = AccessPolicyKind.AnyOperator,
        [GlobalRead] = AccessPolicyKind.GlobalRead,
        [GlobalOperate] = AccessPolicyKind.GlobalOperate,
        [Admin] = AccessPolicyKind.Admin,
    };

    internal static void Register(AuthorizationBuilder builder)
    {
        foreach (var (name, kind) in All)
        {
            builder.AddPolicy(name, policy => policy
                .AddAuthenticationSchemes(UserTokens.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new UiAccessRequirement(kind)));
        }
    }
}

public sealed record UiAccessRequirement(AccessPolicyKind Kind) : IAuthorizationRequirement;

/// <summary>Checks a <see cref="UiAccessRequirement"/> against the grant in the user token.</summary>
public sealed class UiAccessAuthorizationHandler : AuthorizationHandler<UiAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, UiAccessRequirement requirement)
    {
        if (AccessPolicyRules.IsSatisfied(AccessGrantClaims.FromClaims(context.User.Claims), requirement.Kind))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
