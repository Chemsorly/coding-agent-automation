using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering SignalR services including MessagePack protocol
/// and the agent authorization hub filter.
/// </summary>
internal static class SignalRRegistration
{
    /// <summary>
    /// Adds SignalR hub services with MessagePack protocol and agent authorization filter.
    /// </summary>
    public static IServiceCollection AddSignalRServices(this IServiceCollection services)
    {
        services.AddSignalR(options =>
            {
                // Agents may send output chunks or large payloads; default 32KB is too restrictive.
                options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB
            })
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(CompositeResolver.Create(
                        new IMessagePackFormatter[] { new JobIdFormatter(), new ProviderConfigIdFormatter(), new NullableProviderConfigIdFormatter() },
                        new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));
            });

        // Hub filter for agent authorization
        services.AddSingleton<IHubFilter>(sp => new AgentAuthorizationFilter(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));

        return services;
    }
}
