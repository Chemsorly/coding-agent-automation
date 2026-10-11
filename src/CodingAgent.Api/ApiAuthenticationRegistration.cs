using CodingAgent.AgentGateway;
using CodingAgent.Api.Ui;
using CodingAgent.Infrastructure.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api;

/// <summary>
/// Extension methods for registering authentication and authorization for the Pipeline API.
/// Two surfaces (spec 051 D1): the service surface accepts API keys only, the UI surface
/// (<c>/api/ui/**</c>) accepts the user token only. Each policy names its scheme, so a credential
/// of one surface is never evaluated on the other.
/// </summary>
internal static class ApiAuthenticationRegistration
{
    /// <summary>
    /// Adds the agent API key scheme with its <c>AgentApiKey</c>/<c>OperatorApiKey</c> policies,
    /// the user-token scheme with the <see cref="UiPolicies"/>, and a fallback policy that denies
    /// every endpoint that declares no policy. Both keys are supplied directly (fast-fail on a
    /// missing key is handled in Program.cs).
    /// </summary>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        string agentApiKey,
        SecurityKey userTokenSigningKey,
        ILogger logger)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = null;
                options.DefaultChallengeScheme = null;
            })
            .AddScheme<AgentApiKeyAuthOptions, AgentApiKeyAuthHandler>(
                AgentApiKeyDefaults.AuthenticationScheme,
                options => options.ApiKey = agentApiKey)
            .AddJwtBearer(UserTokens.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = UserTokens.CreateValidationParameters(userTokenSigningKey);
            });

        var authorization = services.AddAuthorizationBuilder()
            .AddPolicy(ApiAuthPolicies.Agent, policy =>
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser())
            .AddPolicy(ApiAuthPolicies.Operator, policy =>
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser()
                      .RequireClaim("auth_kind", "operator"))
            // Fail closed: an endpoint mapped without RequireAuthorization (or AllowAnonymous) is
            // denied instead of open. No credential gets a 401, an API key a 403.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(AgentApiKeyDefaults.AuthenticationScheme)
                .RequireAssertion(_ => false)
                .Build());
        UiPolicies.Register(authorization);
        services.AddSingleton<IAuthorizationHandler, UiAccessAuthorizationHandler>();

        services.AddHttpContextAccessor();
        services.AddScoped<IUserAccess, UserAccess>();

        // Register Serilog.ILogger for DI (used by AgentApiKeyAuthHandler and other hub services)
        services.AddSingleton(logger);

        return services;
    }
}
