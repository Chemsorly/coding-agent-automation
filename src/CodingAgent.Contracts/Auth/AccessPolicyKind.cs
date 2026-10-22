namespace CodingAgent.Contracts.Auth;

/// <summary>
/// Grant-only access requirements, shared by the web host's page policies and the Pipeline API's
/// UI-surface policies. Project-level decisions are never a policy: the caller checks the role on
/// the project of the object it acts on.
/// </summary>
public enum AccessPolicyKind
{
    /// <summary>A global role or a role on at least one project.</summary>
    AnyAccess,

    /// <summary>Global operator, or operator on at least one project.</summary>
    AnyOperator,

    /// <summary>Global readonly or higher.</summary>
    GlobalRead,

    /// <summary>Global operator or higher.</summary>
    GlobalOperate,

    /// <summary>Global admin.</summary>
    Admin,

    /// <summary>Global admin, or admin on at least one project.</summary>
    AnyAdmin,
}

/// <summary>Decides whether a grant satisfies an <see cref="AccessPolicyKind"/>.</summary>
public static class AccessPolicyRules
{
    public static bool IsSatisfied(AccessGrant grant, AccessPolicyKind kind) => kind switch
    {
        AccessPolicyKind.AnyAccess => grant.HasAnyAccess,
        AccessPolicyKind.AnyOperator => grant.HasAnyOperator,
        AccessPolicyKind.GlobalRead => grant.GlobalRole >= AccessRole.ReadOnly,
        AccessPolicyKind.GlobalOperate => grant.GlobalRole >= AccessRole.Operator,
        AccessPolicyKind.Admin => grant.GlobalRole == AccessRole.Admin,
        AccessPolicyKind.AnyAdmin => grant.HasAnyAdmin,
        _ => false,
    };
}
