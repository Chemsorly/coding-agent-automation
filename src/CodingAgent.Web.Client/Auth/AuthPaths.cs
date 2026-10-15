namespace CodingAgent.Web.Auth;

/// <summary>Paths and query values of the authentication UI.</summary>
public static class AuthPaths
{
    public const string Login = "/login";
    public const string AccessDenied = "/access-denied";
    public const string Profile = "/user";
    public const string DefaultLanding = "/overview";
    public const string ReturnUrlParameter = "returnUrl";

    /// <summary>The form post that signs the user out (served by the web host).</summary>
    public const string Logout = "/auth/logout";
}
