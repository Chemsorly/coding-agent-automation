using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers agent registry and job deduplication guard services.
    ///
    /// <para>
    /// <b>Two registries, deliberately.</b> Since Spec 044 the Pipeline API process owns agent
    /// presence: it maps <c>AgentHub</c>, and <c>AgentHub.RegisterAgent</c> is the only writer of an
    /// agent registry anywhere in the system. So <see cref="IAgentRegistryService"/> — the interface
    /// every read-only consumer injects, from <c>AgentMonitoring</c> and
    /// <c>SidebarHealthIndicators</c> to the issue/epic/PR drawer services — is bound to
    /// <see cref="ApiAgentRegistryService"/>, which serves a background-refreshed snapshot of
    /// <c>GET /api/agents</c>. Before this, it was bound to a local
    /// <see cref="AgentRegistryService"/> that nothing ever wrote to, so those consumers saw an
    /// empty cluster forever.
    /// </para>
    ///
    /// <para>
    /// The local <see cref="AgentRegistryService"/> singleton stays registered under its concrete
    /// type because <c>ConsolidationDispatchService</c>, <c>ModelFetchService</c>,
    /// <c>AgentChat.razor</c> and <c>RunLifecycleManagerDependencies</c> resolve it directly, and
    /// because it is the instance the E2E factories swap for a resettable one.
    /// </para>
    /// </summary>
    private static void RegisterAgentRegistry(IServiceCollection services)
    {
        services.AddSingleton(sp => new AgentRegistryService(Log.Logger));

        // API-backed read model — the snapshot every UI consumer reads.
        services.AddSingleton(sp => new ApiAgentRegistryService(
            sp.GetRequiredService<IPipelineApiAgentClient>(),
            sp.GetRequiredService<TimeProvider>(),
            Log.Logger));
        services.AddSingleton<IAgentRegistryService>(sp =>
            sp.GetRequiredService<ApiAgentRegistryService>());

        // Background poller that refreshes that snapshot. Without it the registry never leaves its
        // empty initial state and the fix is inert.
        services.AddHostedService(sp => new AgentRegistrySyncService(
            sp.GetRequiredService<ApiAgentRegistryService>(),
            sp.GetRequiredService<TimeProvider>(),
            Log.Logger));

        // Pinned to the LOCAL registry, not IAgentRegistryService. SelectAgent reserves an agent by
        // The monolith's registry is a read-only replica (ApiAgentRegistryService). SelectAgent here
        // is meaningless — dispatch belongs to the API process. Register both the backward-compat
        // alias and AgentReservationService so ConsolidationDispatchDependencies can resolve.
        services.AddSingleton<AgentReservationService>(sp => new AgentReservationService(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));
        services.AddSingleton(sp => new JobDeduplicationGuardService(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));
    }
}
