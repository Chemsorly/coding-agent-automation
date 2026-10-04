using System.Threading.RateLimiting;
using CodingAgent.AgentGateway;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Registers user authentication for the web UI (Spec 049): a cookie session, the local admin
/// login, OIDC (when enabled), the fallback "authenticated user" policy, the login rate limit,
/// forwarded headers and the agent API key scheme (kept for <c>/api/export/runs.json</c>).
/// </summary>
internal static class UserAuthenticationRegistration
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string LoginRateLimitPolicy = "login";

    /// <summary>The agent API key policy name, unchanged from before Spec 049.</summary>
    public const string AgentApiKeyPolicy = "AgentApiKey";

    public static IServiceCollection AddUserAuthentication(
        this IServiceCollection services, IConfiguration configuration, ILogger logger)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        var agentApiKey = AgentApiKeyAuthHandler.ResolveApiKey(logger);
        var authentication = services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = CookieScheme;
            })
            .AddCookie(CookieScheme)
            // Only endpoints with RequireAuthorization("AgentApiKey") use this scheme.
            .AddScheme<AgentApiKeyAuthOptions, AgentApiKeyAuthHandler>(
                AgentApiKeyDefaults.AuthenticationScheme,
                options => options.ApiKey = agentApiKey);
        if (configuration.GetValue<bool>($"{AuthOptions.SectionName}:Oidc:Enabled"))
            authentication.AddOidcLogin();

        // Deferred so the session duration is read from validated options, not at registration.
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<IOptions<AuthOptions>>((cookie, auth) => ConfigureCookie(cookie, auth.Value));

        var authorization = services.AddAuthorizationBuilder();
        authorization
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(CookieScheme).RequireAuthenticatedUser().Build())
            .AddPolicy(AgentApiKeyPolicy, policy =>
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser());
        AccessPolicies.Register(authorization);
        services.AddSingleton<IAuthorizationHandler, AccessAuthorizationHandler>();

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionRevalidatingAuthenticationStateProvider>();
        services.AddSingleton<IRbacEvaluator, RbacEvaluator>();
        services.AddScoped<CurrentAccess>();
        services.AddScoped<IAccessGuard, AccessGuard>();

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(LoginRateLimitPolicy, context =>
            {
                var permits = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginRateLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Redirect(AuthPaths.Login + "?error=" + LoginErrors.RateLimit);
                return ValueTask.CompletedTask;
            };
        });

        // The web Service is ClusterIP behind the ingress, whose pod IPs are not known in advance.
        // Empty KnownIPNetworks/KnownProxies make the middleware accept any forwarder (as
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED does). ForwardLimit 1 reads only the last
        // X-Forwarded-For entry, the one the ingress adds, so a client cannot choose its rate-limit
        // partition through the ingress; it can only by reaching the pod directly, inside the cluster.
        // Read: client IP (rate-limit partition, logs) and protocol (redirect URI, Secure cookie).
        // The host comes from Host.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions cookie, AuthOptions auth)
    {
        cookie.LoginPath = AuthPaths.Login;
        cookie.AccessDeniedPath = AuthPaths.AccessDenied;
        cookie.ReturnUrlParameter = AuthPaths.ReturnUrlParameter;
        cookie.Cookie.Name = "ca_session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = auth.GetSessionDuration();
        cookie.SlidingExpiration = false;

        // The Blazor hub and API-style requests get status codes, not an HTML login redirect.
        cookie.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
        cookie.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
    }

    private static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/api"))
            context.Response.StatusCode = statusCode;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>Paths and query values of the authentication UI.</summary>
public static class AuthPaths
{
    public const string Login = "/login";
    public const string AccessDenied = "/access-denied";
    public const string Profile = "/user";
    public const string DefaultLanding = "/overview";
    public const string ReturnUrlParameter = "returnUrl";
}

/// <summary>Values of the <c>error</c> query parameter of the login page. Never echoed back.</summary>
public static class LoginErrors
{
    public const string Credentials = "credentials";
    public const string RateLimit = "ratelimit";
    public const string Oidc = "oidc";
}
