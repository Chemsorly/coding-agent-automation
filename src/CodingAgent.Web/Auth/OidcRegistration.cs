using CodingAgent.Infrastructure.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Registers the OpenID Connect login (Spec 049 Req 3): authorization code flow with PKCE as a
/// confidential client, claims from the ID token only (no userinfo call), no tokens stored. The
/// code comes back as a query parameter, so the correlation and nonce cookies can stay SameSite=Lax.
/// </summary>
internal static class OidcRegistration
{
    public const string OidcScheme = "oidc";
    public const string CallbackPath = "/signin-oidc";

    public static AuthenticationBuilder AddOidcLogin(this AuthenticationBuilder builder)
    {
        builder.AddOpenIdConnect(OidcScheme, _ => { });

        // Deferred to read validated options; the secret comes from Auth__Oidc__ClientSecret.
        builder.Services.AddOptions<OpenIdConnectOptions>(OidcScheme)
            .Configure<IOptions<AuthOptions>, TimeProvider, ILoggerFactory>((oidc, auth, time, loggers) =>
                Configure(oidc, auth.Value, time, loggers.CreateLogger(typeof(OidcRegistration).FullName!)));
        return builder;
    }

    private static void Configure(OpenIdConnectOptions oidc, AuthOptions auth, TimeProvider time, ILogger logger)
    {
        var provider = auth.Oidc;
        oidc.Authority = provider.Issuer;
        oidc.ClientId = provider.ClientId;
        oidc.ClientSecret = provider.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.MapInboundClaims = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.SaveTokens = false;
        oidc.CallbackPath = CallbackPath;
        oidc.SignInScheme = UserAuthenticationRegistration.CookieScheme;
        oidc.Scope.Clear();
        foreach (var scope in provider.EffectiveScopes)
            oidc.Scope.Add(scope);

        oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
        oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        oidc.NonceCookie.SameSite = SameSiteMode.Lax;
        oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        oidc.Events.OnTokenValidated = context =>
        {
            var expiresAt = time.GetUtcNow() + auth.GetSessionDuration();
            var result = OidcClaimMapper.Map(context.Principal!, provider, expiresAt);
            if (result.GroupOverage)
            {
                logger.LogWarning(
                    "OIDC user '{Username}' has too many groups for the token (group overage); no groups were received. " +
                    "Assign groups to the application or use app roles (see docs/authentication.md)",
                    LogSanitizer.SanitizeForLog(result.Username));
            }

            context.Principal = result.Principal;
            context.Properties!.IsPersistent = true;
            context.Properties.ExpiresUtc = expiresAt;
            context.Properties.AllowRefresh = false;
            logger.LogInformation(
                "Login succeeded for user '{Username}' ({IdentitySource}) from {ClientIp} with {GroupCount} groups",
                LogSanitizer.SanitizeForLog(result.Username), IdentitySources.Oidc,
                context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", result.GroupCount);
            return Task.CompletedTask;
        };

        oidc.Events.OnRemoteFailure = context =>
        {
            logger.LogWarning(context.Failure, "OIDC sign-in with {Provider} failed", LogSanitizer.SanitizeForLog(provider.Name));
            context.Response.Redirect($"{AuthPaths.Login}?error={LoginErrors.Oidc}");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }
}
