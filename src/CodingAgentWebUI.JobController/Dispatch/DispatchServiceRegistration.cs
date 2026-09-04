using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// DI registrations for the Job Controller dispatch services.
///
/// As of issue #2322, the regular <c>DispatchLoop</c> and <c>DispatchService</c> have been removed.
/// The live dispatch path now routes through the new synchronous <c>POST /api/work-items/dispatch</c>
/// endpoint in <c>CodingAgentWebUI.Api</c>. The Job Controller retains its <c>ReconciliationLoop</c>
/// (timeout enforcement, K8s Job status sync, orphan cleanup) and the <c>ConsolidationDispatchLoop</c>
/// (which still uses the Pending-queue path and is out of scope for this migration).
/// </summary>
public static class DispatchServiceRegistration
{
    /// <summary>
    /// Registers <see cref="ConsolidationDispatchService"/> and <see cref="ConsolidationDispatchLoop"/>
    /// using options from configuration.
    ///
    /// <see cref="DispatchLoop"/> and <see cref="DispatchService"/> have been deleted (issue #2322).
    /// The <c>IProviderFactory</c> / <c>ApiPipelineConfigStore</c> / <c>IPipelineConfigStore</c>
    /// registrations that only served <c>DispatchLoop.IsIssueEligible</c> have been removed.
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        // Single process-wide PVC selection lock shared by ConsolidationDispatchLoop.
        // Also guards the cross-loop TOCTOU race if DispatchLoop were ever re-introduced.
        services.AddSingleton<PvcSelectLock>();

        // ── Consolidation work item dispatch ──────────────────────────────
        // Shares the same ILeaderElectionService lease as ReconciliationService — only the leader
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
