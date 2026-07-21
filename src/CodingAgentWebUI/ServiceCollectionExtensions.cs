using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering domain services in the DI container.
/// Extracted from Program.cs to reduce file size and group related registrations.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure services: configuration store interfaces, provider factory, and validation services.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        JsonConfigurationStore configStore,
        PipelineConfiguration pipelineConfig)
    {
        services.AddSingleton<IConfigurationStore>(configStore);
        WorkDistributionRegistration.RegisterConfigStoreSubInterfaces(services);

        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        services.AddTransient<GitHubValidationService>(sp =>
            new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));
        services.AddTransient<GitLabValidationService>();

        return services;
    }

    /// <summary>
    /// Registers infrastructure services WITHOUT config store registrations.
    /// Used in DB mode where PostgresConfigurationStore is registered by AddWorkDistribution.
    /// </summary>
    public static IServiceCollection AddInfrastructureServicesWithoutConfigStore(
        this IServiceCollection services)
    {
        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        services.AddTransient<GitHubValidationService>(sp =>
            new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));
        services.AddTransient<GitLabValidationService>();

        return services;
    }

    /// <summary>
    /// Registers WebUI-specific pipeline services: orchestration, loop service, lifecycle, and history.
    /// In DB mode, skips the in-memory history service registration (PostgresPipelineRunHistoryService
    /// is registered by AddWorkDistribution instead).
    /// </summary>
    public static IServiceCollection AddPipelineCoreServices(this IServiceCollection services, bool isDatabaseMode = false)
    {
        // ── Lifecycle ──────────────────────────────────────────────────────
        RegisterPipelineLifecycle(services, isDatabaseMode);

        // ── Facades ────────────────────────────────────────────────────────
        RegisterPipelineFacades(services);

        // ── Shutdown ───────────────────────────────────────────────────────
        RegisterPipelineShutdown(services);

        // ── Background Services ────────────────────────────────────────────
        RegisterPipelineBackgroundServices(services);

        return services;
    }

    /// <summary>
    /// Registers multi-agent orchestration services: agent registry, job dispatch, token vending,
    /// heartbeat monitoring, and the AgentHub facade.
    /// </summary>
    public static IServiceCollection AddOrchestrationServices(
        this IServiceCollection services,
        PipelineConfiguration pipelineConfig,
        string? workDistributionMode = null)
    {
        // ── Agent Registry ─────────────────────────────────────────────────
        RegisterAgentRegistry(services);

        // ── Token Vending & Run Services ───────────────────────────────────
        RegisterTokenAndRunServices(services, pipelineConfig);

        // ── Conditional Background Services ────────────────────────────────
        RegisterOrchestrationBackgroundServices(services, workDistributionMode);

        // ── Job Dispatching ────────────────────────────────────────────────
        RegisterJobDispatching(services);

        // ── Agent Hub Services ─────────────────────────────────────────────
        RegisterAgentHubServices(services);

        return services;
    }

    /// <summary>
    /// Registers consolidation services: queue, dispatcher, service, and badge service.
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

        services.AddSingleton<IConsolidationDispatcher>(sp => new ConsolidationDispatcher(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<ITokenVendingService>(),
            pipelineConfig,
            sp.GetRequiredService<IWorkDistributor>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            Log.Logger,
            sp.GetRequiredService<IConsolidationRunStore>(),
            sp.GetRequiredService<IConsolidationJobPreparationService>()));

        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            Log.Logger,
            pipelineConfig,
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConsolidationRunStore>(),
            sp.GetRequiredService<IHarnessSuggestionStore>(),
            sp.GetRequiredService<IConsolidationDispatcher>()));

        services.AddSingleton<ConsolidationBadgeService>();
        services.AddSingleton<ProjectChangeNotifier>();

        return services;
    }
}
