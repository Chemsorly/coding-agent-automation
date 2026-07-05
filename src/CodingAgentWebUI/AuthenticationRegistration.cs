using CodingAgentWebUI.Hubs;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering agent API key authentication and authorization.
/// </summary>
internal static class AuthenticationRegistration
{
    /// <summary>
    /// Adds agent API key authentication scheme and the <c>AgentApiKey</c> authorization policy.
    /// The scheme is NOT set as default to avoid interfering with Blazor UI — only endpoints
    /// with <c>RequireAuthorization("AgentApiKey")</c> trigger it.
    /// </summary>
    public static IServiceCollection AddAgentAuthentication(this IServiceCollection services, ILogger logger)
    {
        var agentApiKey = AgentApiKeyAuthHandler.ResolveApiKey(logger);
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = null;
                options.DefaultChallengeScheme = null;
            })
            .AddScheme<AgentApiKeyAuthOptions, AgentApiKeyAuthHandler>(
                AgentApiKeyDefaults.AuthenticationScheme,
                options => options.ApiKey = agentApiKey);
        services.AddAuthorization(options =>
        {
            options.AddPolicy("AgentApiKey", policy =>
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser());
        });

        return services;
    }
}
