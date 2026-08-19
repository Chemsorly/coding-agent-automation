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
            sp.GetRequiredService<ReconciliationLoop>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddHostedService(sp => sp.GetRequiredService<ReconciliationService>());

        return services;
    }
}
