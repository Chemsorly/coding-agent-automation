using CodingAgent.AgentGateway;

namespace CodingAgent.Web;

/// <summary>
/// Extension methods for registering SignalR services including MessagePack protocol
/// and the agent authorization hub filter.
/// </summary>
internal static class SignalRRegistration
{
    /// <summary>
    /// Adds SignalR hub services with MessagePack protocol and agent authorization filter.
    /// Delegates to <see cref="AgentSignalRServiceCollectionExtensions.AddAgentSignalRServices"/>
    /// so the formatter list and filter wiring are defined exactly once in
    /// <c>CodingAgent.AgentGateway</c>.
    /// </summary>
    public static IServiceCollection AddSignalRServices(this IServiceCollection services)
    {
        services.AddAgentSignalRServices();
        return services;
    }
}
