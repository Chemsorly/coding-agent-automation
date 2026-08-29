using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.JobController.Dispatch;
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
    /// Requires <see cref="PvcPool"/> and <see cref="DispatchServiceOptions"/> to already be registered.
    /// Also registers <see cref="IReconciliationTrigger"/> as a forwarding alias to the singleton
    /// <see cref="ReconciliationService"/> instance, allowing <see cref="DispatchLoop"/> and
    /// <see cref="ConsolidationDispatchLoop"/> to request early reconciliation cycles without
    /// depending on the concrete service type.
    /// </summary>
    public static IServiceCollection AddReconciliationService(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationLoop>(sp => new ReconciliationLoop(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<PvcPool>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddSingleton<ReconciliationService>(sp => new ReconciliationService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<ReconciliationLoop>()));

        // Register IReconciliationTrigger as a forwarding alias to the same ReconciliationService
        // singleton. Both DispatchLoop and ConsolidationDispatchLoop depend on this interface
        // to signal an early reconciliation cycle when the PVC pool is exhausted.
        // The factory is lazy — IReconciliationTrigger is resolved only when DispatchLoop
        // is first requested, at which point ReconciliationService is already registered.
        services.AddSingleton<IReconciliationTrigger>(sp => sp.GetRequiredService<ReconciliationService>());

        services.AddHostedService(sp => sp.GetRequiredService<ReconciliationService>());

        return services;
    }
}
