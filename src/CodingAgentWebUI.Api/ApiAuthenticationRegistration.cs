using CodingAgentWebUI.Hub;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Extension methods for registering agent API key authentication and authorization
/// for the Pipeline API.
/// </summary>
internal static class ApiAuthenticationRegistration
{
    /// <summary>
    /// Adds agent API key authentication scheme and the <c>AgentApiKey</c> authorization policy.
    /// The key is supplied directly (fast-fail on missing key handled in Program.cs).
    /// </summary>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        string agentApiKey,
        ILogger logger)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = null;
                options.DefaultChallengeScheme = null;
            })
            .AddScheme<AgentApiKeyAuthOptions, AgentApiKeyAuthHandler>(
                AgentApiKeyDefaults.AuthenticationScheme,
                options => options.ApiKey = agentApiKey);

        services.AddAuthorizationBuilder()
            .AddPolicy("AgentApiKey", policy =>
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser())
            .AddPolicy("OperatorApiKey", policy =>
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser()
                      .RequireClaim("auth_kind", "operator"));

        // Register Serilog.ILogger for DI (used by AgentApiKeyAuthHandler and other hub services)
        services.AddSingleton(logger);

        return services;
    }
}
