using System.Security.Cryptography;
using System.Text;
using CodingAgent.Infrastructure.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Login and logout endpoints (Spec 049 Req 2, 3, 10). An interactive Blazor circuit cannot set
/// cookies, so the login page posts to these plain HTTP endpoints. All are anonymous; the form
/// posts are antiforgery-validated.
/// </summary>
internal static class AuthEndpoints
{
    public const string LoginEndpoint = "/auth/login";
    public const string OidcEndpoint = "/auth/oidc";
    public const string LogoutEndpoint = "/auth/logout";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromForm] binding makes the antiforgery middleware validate the token.
        app.MapPost(LoginEndpoint, LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(UserAuthenticationRegistration.LoginRateLimitPolicy);

        app.MapGet(OidcEndpoint, ChallengeOidc).AllowAnonymous();

        app.MapPost(LogoutEndpoint, LogoutAsync).AllowAnonymous();

        return app;
    }

    /// <summary>Form fields of the local admin login form.</summary>
    internal sealed class LoginForm
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ReturnUrl { get; set; }
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        [FromForm] LoginForm form,
        IOptions<AuthOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var auth = options.Value;
        if (!auth.Admin.Enabled)
            return Results.NotFound();

        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);
        var returnUrl = SafeReturnUrl(form.ReturnUrl);
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Non-short-circuit '&' so a wrong username costs the same as a wrong password.
        var valid = string.Equals(form.Username?.Trim(), LocalAdminOptions.Username, StringComparison.Ordinal)
                    & PasswordMatches(form.Password, auth.Admin.Password ?? "");
        if (!valid)
        {
            logger.LogWarning("Login failed for user '{Username}' from {ClientIp}",
                LogSanitizer.SanitizeForLog(form.Username), clientIp);
            return Results.Redirect(
                $"{AuthPaths.Login}?error={LoginErrors.Credentials}&{AuthPaths.ReturnUrlParameter}={Uri.EscapeDataString(returnUrl)}");
        }

        var expiresAt = time.GetUtcNow() + auth.GetSessionDuration();
        await context.SignInAsync(
            UserAuthenticationRegistration.CookieScheme,
            AuthPrincipals.CreateLocalAdmin(expiresAt),
            SessionProperties(expiresAt));
        logger.LogInformation("Login succeeded for user '{Username}' ({IdentitySource}) from {ClientIp}",
            LocalAdminOptions.Username, IdentitySources.Local, clientIp);

        return Results.LocalRedirect(returnUrl);
    }

    private static IResult ChallengeOidc(string? returnUrl, IOptions<AuthOptions> options)
    {
        if (!options.Value.Oidc.Enabled)
            return Results.NotFound();

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) },
            [OidcRegistration.OidcScheme]);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        // No form binding here, so the antiforgery check must be explicit: the middleware only
        // records the result and minimal APIs reject only when they bind form data.
        if (!await antiforgery.IsRequestValidAsync(context))
            return Results.BadRequest();

        await context.SignOutAsync(UserAuthenticationRegistration.CookieScheme);
        return Results.Redirect(AuthPaths.Login);
    }

    /// <summary>Persistent cookie with a fixed expiry and no refresh (Req 4.2).</summary>
    internal static AuthenticationProperties SessionProperties(DateTimeOffset expiresAt) => new()
    {
        IsPersistent = true,
        ExpiresUtc = expiresAt,
        AllowRefresh = false,
    };

    internal static bool PasswordMatches(string? supplied, string expected)
    {
        // Hash first so the comparison is constant-time regardless of the input lengths.
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? ""));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return !string.IsNullOrEmpty(expected) && CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    /// <summary>
    /// Returns <paramref name="returnUrl"/> when it is a local path that is not part of the
    /// authentication UI; otherwise the default landing page (Req 1.3).
    /// </summary>
    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (!IsLocalUrl(returnUrl))
            return AuthPaths.DefaultLanding;

        var path = returnUrl!;
        if (path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(AuthPaths.Login, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(AuthPaths.AccessDenied, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/signin-oidc", StringComparison.OrdinalIgnoreCase))
        {
            return AuthPaths.DefaultLanding;
        }

        return path;
    }

    /// <summary>Same rules as <c>IUrlHelper.IsLocalUrl</c> for root-relative paths.</summary>
    private static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
            return false;
        if (url.Length == 1)
            return true;
        if (url[1] is '/' or '\\')
            return false;
        return !url.AsSpan(1).ContainsAnyInRange('\0', '\u001F') && !url.Contains('\u007F');
    }
}
