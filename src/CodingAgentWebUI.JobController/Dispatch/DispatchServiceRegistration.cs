using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Api.Client.Stores;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// DI registrations for the Job Controller dispatch services.
/// </summary>
public static class DispatchServiceRegistration
{
    /// <summary>
    /// Registers <see cref="ConsolidationDispatchService"/> and <see cref="ConsolidationDispatchLoop"/>
    /// using options from configuration.
    /// Note: <see cref="DispatchService"/> and <see cref="DispatchLoop"/> have been removed.
    /// Regular work item dispatch is now synchronous via <c>POST /api/work-items/dispatch</c>,
    /// called directly by <c>KubernetesWorkDistributor</c> in the Scheduler process.
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        // Single process-wide PVC selection lock shared by ConsolidationDispatchLoop.
        // Retained for the consolidation path which still uses the Pending-queue pattern.
        services.AddSingleton<PvcSelectLock>();

        // ── Provider factory for issue-eligibility checks ─────────────────────
        // Retained for ConsolidationDispatchLoop which still checks issue eligibility.
        services.AddSingleton<ApiPipelineConfigStore>(sp =>
            new ApiPipelineConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>()));
        services.AddSingleton<IPipelineConfigStore>(sp =>
            sp.GetRequiredService<ApiPipelineConfigStore>());
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── Consolidation work item dispatch ──────────────────────────────────
        // Shares the same ILeaderElectionService lease as DispatchService — only the leader
        // replica dispatches. Stateless: all domain operations delegated to the API via
        // IPipelineApiConsolidationWorkItemClient.
        services.AddSingleton<ConsolidationDispatchLoop>(sp => new ConsolidationDispatchLoop(
            sp.GetRequiredService<IPipelineApiConsolidationWorkItemClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<JobTemplateStore>(),
            sp.GetRequiredService<DispatchServiceOptions>(),
            sp.GetRequiredService<PvcSelectLock>()));

        services.AddSingleton<ConsolidationDispatchService>(sp => new ConsolidationDispatchService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<ConsolidationDispatchLoop>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddHostedService(sp => sp.GetRequiredService<ConsolidationDispatchService>());

        return services;
    }
}
