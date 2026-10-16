namespace CodingAgent.Web.Auth;

/// <summary>What the pages show about the configured sign-in methods (spec 051 5.2), without the auth options.</summary>
public interface IAuthUiInfo
{
    /// <summary>The display name of the OIDC provider, shown for users who signed in through it.</summary>
    string OidcProviderName { get; }
}
