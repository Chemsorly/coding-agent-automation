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
}
