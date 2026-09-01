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

        // ── Provider factory for issue-eligibility checks ─────────────────────
        // ProviderFactory requires IPipelineConfigStore for CreatePipelineProviderAsync, but
        // the eligibility path only calls CreateIssueProvider (which doesn't touch the config
        // store). ApiPipelineConfigStore is the pragmatic safe choice matching the Scheduler pattern.
        // Registration order: concrete first, interface forwarded second (Scheduler convention).
        // TODO: The forwarding-singleton pattern (concrete type registered first, interface forwarded
        // second) is fragile: if a future extension method or library also registers IPipelineConfigStore,
        // GetRequiredService<IPipelineConfigStore>() will silently resolve the last-registered one.
        // This is consistent with the Scheduler's pattern, but worth noting if the JobController host
        // DI container is extended further.
        services.AddSingleton<ApiPipelineConfigStore>(sp =>
            new ApiPipelineConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>()));
        services.AddSingleton<IPipelineConfigStore>(sp =>
            sp.GetRequiredService<ApiPipelineConfigStore>());
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── Regular work item dispatch ────────────────────────────────────────
        services.AddSingleton<DispatchLoop>(sp => new DispatchLoop(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<JobTemplateStore>(),
            sp.GetRequiredService<DispatchServiceOptions>(),
            sp.GetRequiredService<PvcSelectLock>(),
            sp.GetRequiredService<IProviderFactory>()));

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
            sp.GetRequiredService<PvcSelectLock>()));

        services.AddSingleton<ConsolidationDispatchService>(sp => new ConsolidationDispatchService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<ConsolidationDispatchLoop>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddHostedService(sp => sp.GetRequiredService<ConsolidationDispatchService>());

        return services;
    }
}
