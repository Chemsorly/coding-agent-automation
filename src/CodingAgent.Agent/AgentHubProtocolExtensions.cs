using CodingAgent.Pipeline;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Agent;

/// <summary>
/// Client-side counterpart of the hub's <c>AddAgentSignalRCore</c>.
/// </summary>
public static class AgentHubProtocolExtensions
{
    /// <summary>
    /// Adds the MessagePack protocol with <see cref="AgentHubMessagePack.SerializerOptions"/> — the
    /// serializer configuration the hub uses — so arguments bind to the hub's parameter types.
    /// </summary>
    public static IHubConnectionBuilder AddAgentHubProtocol(this IHubConnectionBuilder builder) =>
        builder.AddMessagePackProtocol(options => options.SerializerOptions = AgentHubMessagePack.SerializerOptions);
}
