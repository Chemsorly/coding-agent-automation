using Microsoft.Extensions.Options;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Startup validation of <see cref="AuthOptions"/> (Req 1.4, 2.7, 3.9, 4.2, 5.3, 5.6). Registered
/// with <c>ValidateOnStart</c>, so an invalid configuration stops the host before it serves.
/// </summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();

        if (!options.Admin.Enabled && !options.Oidc.Enabled)
            errors.Add("Auth: enable the local admin (Auth:Admin:Enabled) or OIDC (Auth:Oidc:Enabled). Authentication cannot be disabled.");

        if (options.Admin.Enabled && string.IsNullOrEmpty(options.Admin.Password))
            errors.Add("Auth:Admin:Password is empty while the local admin is enabled. Set the Auth__Admin__Password environment variable or disable the local admin.");

        if (options.Oidc.Enabled)
            ValidateOidc(options.Oidc, errors);

        if (!SessionDurationParser.TryParse(options.SessionDuration, out _))
            errors.Add($"Auth:SessionDuration '{options.SessionDuration}' is not a positive duration. Use '<n>h', '<n>m' or a TimeSpan such as '1.00:00:00'.");

        if (options.LoginRateLimitPerMinute <= 0)
            errors.Add("Auth:LoginRateLimitPerMinute must be greater than 0.");

        ValidateRbac(options.Rbac, errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateOidc(OidcProviderOptions oidc, List<string> errors)
    {
        // Scheme check: on Linux "/realms/x" parses as an absolute file:// URI.
        if (!Uri.TryCreate(oidc.Issuer, UriKind.Absolute, out var issuer)
            || (issuer.Scheme != Uri.UriSchemeHttps && issuer.Scheme != Uri.UriSchemeHttp))
            errors.Add("Auth:Oidc:Issuer must be an absolute http(s) URL when OIDC is enabled.");
        if (string.IsNullOrWhiteSpace(oidc.ClientId))
            errors.Add("Auth:Oidc:ClientId is required when OIDC is enabled.");
        if (string.IsNullOrEmpty(oidc.ClientSecret))
            errors.Add("Auth:Oidc:ClientSecret is empty while OIDC is enabled. Set the Auth__Oidc__ClientSecret environment variable.");
        if (string.IsNullOrWhiteSpace(oidc.UsernameClaim))
            errors.Add("Auth:Oidc:UsernameClaim must not be empty.");
        if (string.IsNullOrWhiteSpace(oidc.GroupsClaim))
            errors.Add("Auth:Oidc:GroupsClaim must not be empty.");
    }

    private static void ValidateRbac(RbacOptions rbac, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(rbac.DefaultRole)
            && (!AccessRoleNames.TryParse(rbac.DefaultRole, out var defaultRole) || defaultRole == AccessRole.Admin))
        {
            errors.Add($"Auth:Rbac:DefaultRole '{rbac.DefaultRole}' is invalid. Use '', '{AccessRoleNames.ReadOnly}' or '{AccessRoleNames.Operator}'.");
        }

        for (var i = 0; i < rbac.Bindings.Count; i++)
        {
            var binding = rbac.Bindings[i];
            var prefix = $"Auth:Rbac:Bindings:{i}";
            var hasGroup = !string.IsNullOrWhiteSpace(binding.Group);
            var hasUser = !string.IsNullOrWhiteSpace(binding.User);

            if (hasGroup == hasUser)
                errors.Add($"{prefix} must set exactly one of 'group' and 'user'.");

            if (!AccessRoleNames.TryParse(binding.Role, out var role))
                errors.Add($"{prefix} has unknown role '{binding.Role}'. Use '{AccessRoleNames.ReadOnly}', '{AccessRoleNames.Operator}' or '{AccessRoleNames.Admin}'.");
            else if (role == AccessRole.Admin && !string.IsNullOrWhiteSpace(binding.Project))
                errors.Add($"{prefix} binds '{AccessRoleNames.Admin}' to project '{binding.Project}'. The admin role can only be bound globally.");
        }
    }
}
