using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.JobController.Reconciliation;

/// <summary>
/// DI registrations for the Job Controller reconciliation services.
/// </summary>
public static class ReconciliationServiceRegistration
{
    /// <summary>
    /// Registers <see cref="ReconciliationService"/> and <see cref="ReconciliationLoop"/>.
    /// Requires <see cref="DispatchServiceOptions"/> to already be registered.
    /// Also registers <see cref="IReconciliationTrigger"/> as a forwarding alias to the singleton
    /// <see cref="ReconciliationService"/> instance.
    /// </summary>
    public static IServiceCollection AddReconciliationService(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationLoop>(sp => new ReconciliationLoop(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddSingleton<ReconciliationService>(sp => new ReconciliationService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<ReconciliationLoop>()));

        // Register IReconciliationTrigger as a forwarding alias to the same ReconciliationService
        // singleton. Previously used by DispatchLoop and ConsolidationDispatchLoop (both removed)
        // to signal early reconciliation cycles. The registration is kept because
        // ReconciliationService implements IReconciliationTrigger and the interface may be used
        // by future dispatch components.
        // The factory is lazy — IReconciliationTrigger is resolved only when first requested,
        // at which point ReconciliationService is already registered.
        services.AddSingleton<IReconciliationTrigger>(sp => sp.GetRequiredService<ReconciliationService>());

        services.AddHostedService(sp => sp.GetRequiredService<ReconciliationService>());

        return services;
    }
}
