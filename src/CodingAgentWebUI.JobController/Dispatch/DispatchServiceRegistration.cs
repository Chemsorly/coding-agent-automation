using CodingAgentWebUI.Api.Client;
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
    /// Registers <see cref="DispatchService"/>, <see cref="DispatchLoop"/>, and <see cref="PvcPool"/>
    /// using options from configuration.
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        services.AddSingleton<PvcPool>(sp =>
        {
            // PvcPool starts populated with all configured PVC names (all unclaimed).
            // The claimed-set is rebuilt from live K8s Jobs on each leadership acquisition
            // (DispatchService.RunLeadershipTermAsync → PvcPool.RebuildFromLiveJobsAsync),
            // preventing re-claim of PVCs already assigned to running agent pods after a restart.
            var opts = sp.GetRequiredService<DispatchServiceOptions>();
            return new PvcPool(opts.KiroPvcPool);
        });

        services.AddSingleton<DispatchLoop>(sp => new DispatchLoop(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            sp.GetRequiredService<JobTemplateStore>(),
            sp.GetRequiredService<PvcPool>(),
            sp.GetRequiredService<DispatchServiceOptions>()));

        services.AddSingleton<DispatchService>(sp => new DispatchService(
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<DispatchLoop>(),
            sp.GetRequiredService<DispatchServiceOptions>(),
            sp.GetRequiredService<PvcPool>(),
            sp.GetRequiredService<IKubernetesJobClient>()));

        services.AddHostedService(sp => sp.GetRequiredService<DispatchService>());

        return services;
    }
}
