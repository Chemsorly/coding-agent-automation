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
    /// <para>
    /// Regular work item dispatch (<c>DispatchService</c> / <c>DispatchLoop</c>) has been removed.
    /// <c>KubernetesWorkDistributor</c> now calls <c>POST /api/work-items/dispatch</c> directly,
    /// which atomically creates the K8s Job and transitions the WorkItem to <c>Dispatched</c>
    /// without passing through the <c>Pending</c> queue.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        // Single process-wide PVC selection lock shared by ConsolidationDispatchLoop.
        services.AddSingleton<PvcSelectLock>();

        // ── Provider factory for issue-eligibility checks ─────────────────────
        // ProviderFactory requires IPipelineConfigStore for CreatePipelineProviderAsync, but
        // the eligibility path only calls CreateIssueProvider (which doesn't touch the config
        // store). ApiPipelineConfigStore is the pragmatic safe choice matching the Scheduler pattern.
        // Registration order: concrete first, interface forwarded second (Scheduler convention).
        // TODO: The forwarding-singleton pattern is fragile if a future caller also registers
        // IPipelineConfigStore — DI will silently resolve the last-registered one.
        services.AddSingleton<ApiPipelineConfigStore>(sp =>
            new ApiPipelineConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>()));
        services.AddSingleton<IPipelineConfigStore>(sp =>
            sp.GetRequiredService<ApiPipelineConfigStore>());
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── Consolidation work item dispatch ──────────────────────────────────
        // Shares the same ILeaderElectionService lease as before — only the leader
        // replica dispatches. Stateless: all domain operations delegated to the API via
        // IPipelineApiConsolidationWorkItemClient.
        // Regular item dispatch (DispatchLoop) has been removed — see doc comment above.
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
