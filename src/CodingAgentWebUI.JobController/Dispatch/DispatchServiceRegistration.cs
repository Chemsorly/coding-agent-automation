using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// DI registrations for the Job Controller dispatch services.
///
/// Issue #2322: <see cref="DispatchService"/> and <see cref="DispatchLoop"/> have been removed.
/// Work-item dispatch now uses the synchronous <c>POST /api/work-items/dispatch</c> endpoint
/// in the Pipeline API, which atomically creates the K8s Job and sets WorkItem status to
/// Dispatched in a single request. The Job Controller retains only reconciliation responsibilities.
///
/// <see cref="ConsolidationDispatchService"/> and <see cref="ConsolidationDispatchLoop"/> are
/// NOT yet migrated (deferred to a follow-up issue) and are preserved unchanged.
/// </summary>
public static class DispatchServiceRegistration
{
    /// <summary>
    /// Registers <see cref="ConsolidationDispatchService"/> and <see cref="ConsolidationDispatchLoop"/>
    /// using options from configuration.
    ///
    /// <see cref="DispatchService"/> and <see cref="DispatchLoop"/> are no longer registered —
    /// regular work-item dispatch now uses the synchronous dispatch endpoint on the Pipeline API
    /// (issue #2322).
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        // Single process-wide PVC selection lock shared by ConsolidationDispatchLoop.
        // (Previously also shared with DispatchLoop — that class has been removed.)
        services.AddSingleton<PvcSelectLock>();

        // ── Consolidation work item dispatch ──────────────────────────────────
        // Shares the same ILeaderElectionService lease as ReconciliationService.
        // Stateless: all domain operations delegated to the API via
        // IPipelineApiConsolidationWorkItemClient.
        // TODO: Migrate ConsolidationDispatchLoop to the synchronous dispatch path
        // (follow-up to issue #2322).
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
