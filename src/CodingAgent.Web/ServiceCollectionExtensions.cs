using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.GitLab;
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
        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        services.AddTransient<GitHubValidationService>(sp =>
            new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));
        services.AddTransient<GitLabValidationService>();

        return services;
    }

    /// <summary>
    /// Registers WebUI-specific pipeline services: lifecycle, facades, shutdown, and background services.
    /// Config stores are registered by <see cref="AddWorkDistribution"/> and <c>RegisterPipelineBackgroundServices</c>.
    /// </summary>
    public static IServiceCollection AddPipelineCoreServices(this IServiceCollection services)
    {
        // ── Lifecycle ──────────────────────────────────────────────────────
        RegisterPipelineLifecycle(services);

        // ── Facades ────────────────────────────────────────────────────────
        RegisterPipelineFacades(services);

        // ── Shutdown ───────────────────────────────────────────────────────
        RegisterPipelineShutdown(services);

        // ── Background Services ────────────────────────────────────────────
        RegisterPipelineBackgroundServices(services);

        return services;
    }

    /// <summary>
    /// Registers multi-agent orchestration services: agent registry, job dispatch,
    /// token vending, and dispatch infrastructure.
    /// HeartbeatMonitorService was deleted at Spec 041–045 arc close — agent timeouts
    /// are enforced by ReconciliationService in JobController.
    /// </summary>
    public static IServiceCollection AddOrchestrationServices(
        this IServiceCollection services,
        PipelineConfiguration pipelineConfig)
    {
        // ── Agent Registry ─────────────────────────────────────────────────
        RegisterAgentRegistry(services);

        // ── Token Vending & Run Services ───────────────────────────────────
        RegisterTokenAndRunServices(services, pipelineConfig);

        // ── Background Services ────────────────────────────────────────────
        RegisterOrchestrationBackgroundServices(services);

        // ── Job Dispatching ────────────────────────────────────────────────
        RegisterJobDispatching(services);

        return services;
    }

    /// <summary>
    /// Registers consolidation services: queue, dispatcher, workspace manager, feedback cache, service, and badge service.
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

        services.AddSingleton<IConsolidationWorkspaceManager>(sp =>
            new ConsolidationWorkspaceManager(Log.Logger, pipelineConfig));

        services.AddSingleton<IConsolidationFeedbackCache>(sp =>
            new ConsolidationFeedbackCache(
                Log.Logger,
                sp.GetRequiredService<IConsolidationRunStore>(),
                sp.GetRequiredService<IPipelineRunHistoryService>()));

        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            new Pipeline.Models.ConsolidationServiceDependencies(
                Log.Logger,
                pipelineConfig,
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IConsolidationRunStore>(),
                sp.GetRequiredService<IHarnessSuggestionStore>(),
                sp.GetRequiredService<IConsolidationWorkspaceManager>(),
                sp.GetRequiredService<IConsolidationFeedbackCache>())));

        services.AddSingleton<IConsolidationRunTracker>(sp =>
            (IConsolidationRunTracker)sp.GetRequiredService<IConsolidationService>());

        services.AddSingleton<ConsolidationBadgeService>();

        return services;
    }
}
