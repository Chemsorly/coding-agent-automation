namespace CodingAgent.Web.Auth;

/// <summary>The built-in roles, ordered: a higher role includes every lower one.</summary>
public enum AccessRole
{
    None = 0,
    ReadOnly = 1,
    Operator = 2,
    Admin = 3,
}

/// <summary>Configuration names of <see cref="AccessRole"/> values.</summary>
public static class AccessRoleNames
{
    public const string ReadOnly = "readonly";
    public const string Operator = "operator";
    public const string Admin = "admin";

    /// <summary>Parses a configured role name (case-insensitive). Empty is not a role.</summary>
    public static bool TryParse(string? name, out AccessRole role)
    {
        role = name?.Trim().ToLowerInvariant() switch
        {
            ReadOnly => AccessRole.ReadOnly,
            Operator => AccessRole.Operator,
            Admin => AccessRole.Admin,
            _ => AccessRole.None,
        };
        return role != AccessRole.None;
    }

    /// <summary>Display name of a role; "none" for <see cref="AccessRole.None"/>.</summary>
    public static string ToName(AccessRole role) => role switch
    {
        AccessRole.ReadOnly => ReadOnly,
        AccessRole.Operator => Operator,
        AccessRole.Admin => Admin,
        _ => "none",
    };
}
