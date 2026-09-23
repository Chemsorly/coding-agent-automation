using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Shared SignalR registration for the AgentHub — called by both the API host and the Web host.
/// Centralises the MessagePack protocol registration, MaximumReceiveMessageSize,
/// AgentAuthorizationFilter hub-filter wiring, and the AgentAuthorizationFilter singleton
/// registration so the two hosts cannot diverge on wire format or filter configuration.
/// </summary>
public static class AgentSignalRServiceCollectionExtensions
{
    /// <summary>
    /// Adds SignalR with the canonical AgentHub configuration: MessagePack protocol (with
    /// <see cref="AgentHubMessagePack.SerializerOptions"/>), 128 KB message size
    /// cap, and <see cref="AgentAuthorizationFilter"/> installed via <c>AddFilter&lt;T&gt;()</c>.
    /// Also registers <see cref="AgentAuthorizationFilter"/> as a singleton so SignalR can
    /// resolve it by concrete type from the DI container.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="environment">
    /// The hosting environment. When <see cref="IHostEnvironment.IsDevelopment"/> is <c>true</c>,
    /// <see cref="HubOptions.EnableDetailedErrors"/> is set so that the full server-side exception
    /// message is forwarded to the SignalR client. In all other environments the message is stripped
    /// by ASP.NET Core to avoid leaking internals.
    /// </param>
    /// <returns>
    /// The <see cref="ISignalRServerBuilder"/> so callers can chain optional backplane
    /// configuration (e.g. <c>.AddStackExchangeRedis(...)</c>) on top.
    /// </returns>
    public static ISignalRServerBuilder AddAgentSignalRServices(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        // Hub filter for agent authorization — resolved by AddFilter<AgentAuthorizationFilter>()
        // below, so it must be registered under its concrete type. Registration is placed before
        // AddSignalR so the singleton is available when SignalR resolves filters during the first
        // hub connection.
        // TODO: This registration MUST use the concrete type AgentAuthorizationFilter, NOT
        // services.AddSingleton<IHubFilter, AgentAuthorizationFilter>(...). AddFilter<T>()
        // resolves by T (concrete type), not by IHubFilter — changing to the interface-keyed
        // registration would silently stop the filter from activating with no compile-time error.
        services.AddSingleton(sp => new AgentAuthorizationFilter(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));

        return services
            .AddSignalR(options =>
            {
                // Agents may send output chunks or large payloads; default 32 KB is too restrictive.
                options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB
                // In Development, forward the full server-side exception message to the SignalR
                // client so that HubException details are visible in agent logs. In all other
                // environments ASP.NET Core strips the message to avoid leaking internals.
                options.EnableDetailedErrors = environment.IsDevelopment();
                // AddFilter is the ONLY way to activate a hub filter. Registering
                // AgentAuthorizationFilter as IHubFilter in DI does not install it —
                // the dispatcher reads HubOptions.HubFilters, which only AddFilter populates.
                options.AddFilter<AgentAuthorizationFilter>();
            })
            .AddAgentSignalRCore();
    }

    /// <summary>
    /// Adds the MessagePack protocol with <see cref="AgentHubMessagePack.SerializerOptions"/> — the
    /// serializer configuration the agent also uses — so strongly-typed ID wrappers are serialised
    /// as bare strings on the wire, maintaining wire compatibility with all existing clients.
    /// </summary>
    public static ISignalRServerBuilder AddAgentSignalRCore(this ISignalRServerBuilder builder)
    {
        return builder.AddMessagePackProtocol(options =>
            options.SerializerOptions = AgentHubMessagePack.SerializerOptions);
    }
}
