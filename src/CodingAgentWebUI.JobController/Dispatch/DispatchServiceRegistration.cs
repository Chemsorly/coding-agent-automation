using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.JobController.Reconciliation;
using CodingAgentWebUI.Kubernetes;
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
    /// Registers <see cref="DispatchService"/>, <see cref="DispatchLoop"/>,
    /// <see cref="ConsolidationDispatchService"/>, and <see cref="ConsolidationDispatchLoop"/>
    /// using options from configuration.
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        // Single process-wide PVC selection lock shared by DispatchLoop and ConsolidationDispatchLoop.
        // This prevents the two loops from racing each other and selecting the same free PVC
        // concurrently (cross-loop TOCTOU). See PvcSelectLock for details.
        services.AddSingleton<PvcSelectLock>();

        // ── Regular work item dispatch ────────────────────────────────────────
        services.AddSingleton<DispatchLoop>(sp => new DispatchLoop(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<JobTemplateStore>(),
            sp.GetRequiredService<DispatchServiceOptions>(),
            sp.GetRequiredService<IReconciliationTrigger>(),
            sp.GetRequiredService<PvcSelectLock>()));

        services.AddSingleton<DispatchService>(sp => new DispatchService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<DispatchLoop>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddHostedService(sp => sp.GetRequiredService<DispatchService>());

        // ── Consolidation work item dispatch ──────────────────────────────────
        // Shares the same ILeaderElectionService lease as DispatchService — only the leader
        // replica dispatches. Stateless: all domain operations delegated to the API via
        // IPipelineApiConsolidationWorkItemClient.
        services.AddSingleton<ConsolidationDispatchLoop>(sp => new ConsolidationDispatchLoop(
            sp.GetRequiredService<IPipelineApiConsolidationWorkItemClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<JobTemplateStore>(),
            sp.GetRequiredService<DispatchServiceOptions>(),
            sp.GetRequiredService<IReconciliationTrigger>(),
            sp.GetRequiredService<PvcSelectLock>()));

        services.AddSingleton<ConsolidationDispatchService>(sp => new ConsolidationDispatchService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<ConsolidationDispatchLoop>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddHostedService(sp => sp.GetRequiredService<ConsolidationDispatchService>());

        return services;
    }
}
