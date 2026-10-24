using CodingAgent.Infrastructure;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CodingAgent.Orchestration;

/// <summary>
/// Shared DI registration for the five orchestration services used by both the API and the
/// Scheduler hosts:
/// <list type="bullet">
///   <item><see cref="IProviderFactory"/> → <see cref="ProviderFactory"/></item>
///   <item><see cref="IConsolidationService"/> → <see cref="ConsolidationService"/> (Spec 051 2.4)</item>
///   <item><see cref="IDependencyChecker"/> → <see cref="DependencyChecker"/> (Spec 051 2.3b)</item>
///   <item><see cref="IDispatchOrchestrationService"/> → <see cref="DispatchOrchestrationService"/> (Spec 051 2.3c)</item>
///   <item><see cref="ConsolidationStalenessService"/> — powers auto-start counters (Spec 051 2.4b, issue #3592)</item>
/// </list>
/// Requires the following to already be registered by the caller before the provider is built:
/// <list type="bullet">
///   <item><see cref="IPipelineConfigStore"/></item>
///   <item><see cref="IProjectStore"/></item>
///   <item><see cref="IWorkDistributor"/></item>
///   <item><see cref="DispatchInfrastructure"/></item>
///   <item><see cref="IProviderConfigStore"/></item>
///   <item><see cref="IPipelineRunHistoryService"/></item>
/// </list>
/// Does not register any <see cref="Microsoft.Extensions.Hosting.IHostedService"/> (background
/// loops run only on the elected Scheduler leader; the API runs no loops).
/// Does not reference <c>CodingAgent.Infrastructure.Persistence</c> (layer boundary enforced by
/// <c>LayerBoundaryTests.Orchestration_ShouldNot_ReferenceInfrastructurePersistenceAssembly</c>).
/// Used by both the API and the Scheduler to avoid duplicating identical registration blocks
/// across processes, modelled on <see cref="TokenVendingServiceCollectionExtensions"/>.
/// </summary>
public static class SharedOrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the five shared orchestration services as singletons.
    /// </summary>
    public static IServiceCollection AddSharedOrchestrationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── IProviderFactory ─────────────────────────────────────────────────
        // Not registered by Infrastructure — must be explicit here.
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── IConsolidationService (Spec 051 2.4) ─────────────────────────────
        // The API triggers consolidation for the Consolidation page. Before 2.4a it only
        // saved harness suggestions for the hub and had no IWorkDistributor, so it registered a
        // form that could not trigger. A trigger enqueues through ApiWorkDistributor (API) or
        // KubernetesWorkDistributor (Scheduler) and reads the saved settings from
        // IPipelineConfigStore; the empty configuration is only the fallback for a host without
        // that store.
        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            new ConsolidationServiceDependencies(
                Log.Logger,
                new PipelineConfiguration(),
                sp.GetRequiredService<IProjectStore>(),
                WorkDistributor: sp.GetRequiredService<IWorkDistributor>(),
                PipelineConfigStore: sp.GetRequiredService<IPipelineConfigStore>())));

        // ── IDependencyChecker (Spec 051 2.3b) ──────────────────────────────
        // The pages' provider browsing runs here with the stored credentials.
        services.AddSingleton<IDependencyChecker>(_ => new DependencyChecker(Log.Logger));

        // ── IDispatchOrchestrationService (Spec 051 2.3c) ───────────────────
        // The drawers' dispatch. Third argument is IProviderConfigStore — not IConfigurationStore.
        // Although IConfigurationStore extends IProviderConfigStore, resolving IProviderConfigStore
        // directly keeps this extension free of the broader IConfigurationStore contract and avoids
        // an implicit coupling to its Scheduler-only registration.
        services.AddSingleton<IDispatchOrchestrationService>(sp => new DispatchOrchestrationService(
            new DispatchOrchestrationServiceDependencies(
                sp.GetRequiredService<DispatchInfrastructure>(),
                sp.GetRequiredService<IWorkDistributor>(),
                sp.GetRequiredService<IProviderConfigStore>()),
            Log.Logger));

        // ── ConsolidationStalenessService (Spec 051 2.4b, issue #3592) ──────
        // Powers the auto-start counters shown on the Consolidation page (API) and drives
        // AutoConsolidationService (Scheduler).
        services.AddSingleton<ConsolidationStalenessService>(sp =>
            new ConsolidationStalenessService(
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IProviderConfigStore>(),
                sp.GetRequiredService<IProviderFactory>(),
                Log.Logger));

        return services;
    }
}
