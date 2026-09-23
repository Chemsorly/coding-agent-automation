using CodingAgent.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace CodingAgent.Pipeline;

/// <summary>
/// The MessagePack serializer configuration for the agent hub (<see cref="HubRoutes.Agent"/>).
/// This is the only copy: the hub (<c>AddAgentSignalRCore</c>) and the agent (<c>AddAgentHubProtocol</c>)
/// both use it, so the two ends of the connection cannot drift apart.
/// </summary>
/// <remarks>
/// Every strongly-typed ID that crosses the hub needs a formatter in this list. Without one,
/// <see cref="ContractlessStandardResolverAllowPrivate"/> writes the struct as a map
/// (<c>{"Value":"..."}</c>), which cannot bind to a hub parameter declared as <c>string</c>.
/// </remarks>
public static class AgentHubMessagePack
{
    /// <summary>Serializer options shared by both ends of the agent hub connection.</summary>
    public static MessagePackSerializerOptions SerializerOptions { get; } =
        MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(
            new IMessagePackFormatter[] { new JobIdFormatter(), new AgentIdFormatter() },
            new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));
}
