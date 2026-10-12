using CodingAgent.Infrastructure;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.AgentGateway;
using CodingAgent.Web.Services;
using Serilog;

namespace CodingAgent.Web;

/// <summary>
/// Extension methods for registering domain services in the DI container.
/// Extracted from Program.cs to reduce file size and group related registrations.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure services. Config stores are registered by
    /// <see cref="AddWorkDistribution"/> and <c>RegisterPipelineBackgroundServices</c>.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services)
    {
        // Only the label service still takes it: the drawers browse and dispatch through the API since
        // spec 051 2.3, and Settings' provider checks since 2.1c. Both go with the orchestration
        // services in 4.1.
        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        return services;
    }

    /// <summary>
    /// Registers WebUI-specific pipeline services: shutdown and background services.
    /// Config stores are registered by <see cref="AddWorkDistribution"/> and <c>RegisterPipelineBackgroundServices</c>.
    /// </summary>
    public static IServiceCollection AddPipelineCoreServices(this IServiceCollection services)
    {
        // ── Shutdown ───────────────────────────────────────────────────────
        RegisterPipelineShutdown(services);

        // ── Background Services ────────────────────────────────────────────
        RegisterPipelineBackgroundServices(services);

        return services;
    }

    /// <summary>
    /// Registers multi-agent orchestration services: agent registry and token vending.
    /// HeartbeatMonitorService was deleted at Spec 041–045 arc close — agent timeouts
    /// are enforced by ReconciliationService in JobController.
    /// </summary>
    public static IServiceCollection AddOrchestrationServices(this IServiceCollection services)
    {
        // ── Agent Registry ─────────────────────────────────────────────────
        RegisterAgentRegistry(services);

        // ── Token Vending & Run Services ───────────────────────────────────
        RegisterTokenAndRunServices(services);

        // ── Background Services ────────────────────────────────────────────
        RegisterOrchestrationBackgroundServices(services);

        return services;
    }

    /// <summary>
    /// Registers consolidation services: job preparation, selector resolver, service, and badge service.
    /// </summary>
    public static IServiceCollection AddConsolidationServices(
        this IServiceCollection services,
        PipelineConfiguration pipelineConfig)
    {
        services.AddSingleton<IConsolidationJobPreparationService>(sp => new ConsolidationJobPreparationService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<ITokenVendingService>(),
            Log.Logger));

        // Resolves agent selector labels for consolidation dispatch.
        // Implemented in CodingAgent.Web so IAgentProfileStore and IPipelineConfigStore
        // stay out of CodingAgent.Pipeline.
        services.AddSingleton<IConsolidationSelectorResolver>(sp =>
            new ConsolidationSelectorResolver(
                sp.GetRequiredService<IAgentProfileStore>(),
                sp.GetRequiredService<IPipelineConfigStore>()));

        // IWorkDistributor is registered by AddWorkDistribution (called immediately after
        // AddConsolidationServices in Program.cs). The factory lambda resolves lazily at
        // first use, so ordering at startup is safe.
        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            new Pipeline.Models.ConsolidationServiceDependencies(
                Log.Logger,
                pipelineConfig,
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<IHarnessSuggestionStore>(),
                sp.GetRequiredService<IProviderConfigStore>(),
                WorkDistributor: sp.GetRequiredService<IWorkDistributor>(),
                SelectorResolver: sp.GetRequiredService<IConsolidationSelectorResolver>(),
                PipelineConfigStore: sp.GetRequiredService<IPipelineConfigStore>())));

        return services;
    }
}
