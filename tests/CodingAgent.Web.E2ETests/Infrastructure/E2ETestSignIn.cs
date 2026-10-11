using CodingAgent.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Spec 049: role bindings of the E2E web host and a test-only <c>/test/sign-in</c> endpoint that
/// issues the real session cookie for an OIDC-shaped principal. The E2E suite has no identity
/// provider; the local admin still signs in through the real login form.
/// </summary>
public static class E2ETestSignIn
{
    /// <summary>Bound to global <c>readonly</c>.</summary>
    public const string ReaderUser = "e2e-reader";

    /// <summary>Bound to <c>operator</c> on the seeded "Default" project.</summary>
    public const string TeamGroup = "e2e-team";

    private static readonly (string Name, string Value)[] Bindings =
    [
        ("Auth__Rbac__Bindings__0__User", ReaderUser),
        ("Auth__Rbac__Bindings__0__Role", "readonly"),
        ("Auth__Rbac__Bindings__1__Group", TeamGroup),
        ("Auth__Rbac__Bindings__1__Role", "operator"),
        ("Auth__Rbac__Bindings__1__ProjectId", "00000000-0000-0000-0000-000000000000"),
    ];

    public static void ApplyBindings()
    {
        foreach (var (name, value) in Bindings)
            Environment.SetEnvironmentVariable(name, value);
    }

    public static void ClearBindings()
    {
        foreach (var (name, _) in Bindings)
            Environment.SetEnvironmentVariable(name, null);
    }

    /// <summary>The URL that signs the browser in as <paramref name="user"/> with <paramref name="groups"/>.</summary>
    public static string SignInUrl(string serverAddress, string user, params string[] groups) =>
        $"{serverAddress}/test/sign-in?user={Uri.EscapeDataString(user)}&groups={Uri.EscapeDataString(string.Join(',', groups))}";

    /// <summary>
    /// Maps <c>/test/sign-in</c>. A plain path check, not app.Map: Map moves the path into
    /// PathBase and the cookie handler would scope the session cookie to it.
    /// </summary>
    public sealed class StartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path != "/test/sign-in")
                {
                    await nextMiddleware();
                    return;
                }

                var user = context.Request.Query["user"].ToString();
                var groups = context.Request.Query["groups"].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{user}", user, null, null, groups, expiresAt),
                    new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expiresAt });
                context.Response.Redirect("/user");
            });
            next(app);
        };
    }
}
