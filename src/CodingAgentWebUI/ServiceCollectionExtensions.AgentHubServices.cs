using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="AgentMonitoringPageServiceDependencies"/> record needed
    /// by <see cref="AgentMonitoringPageService"/>.
    /// T19 (arch-audit 2026-08-22): ApiBackedPendingWorkQuery registered so the job-queue
    /// panel on the Agent Monitoring page shows queued pipeline jobs.
    /// </summary>
    public static IServiceCollection AddAgentMonitoringPageServiceDependencies(this IServiceCollection services)
    {
        services.AddSingleton<IPendingWorkQuery>(sp =>
        {
            var client = sp.GetService<IPipelineApiWorkItemClient>();
            if (client is null) return new EmptyPendingWorkQuery();
            return new ApiBackedPendingWorkQuery(client);
        });

        services.AddScoped(sp => new AgentMonitoringPageServiceDependencies(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IPendingWorkQuery>(),
            sp.GetRequiredService<IWorkDistributor>(),
            sp.GetRequiredService<IPipelineApiRunHistoryClient>(),
            sp.GetRequiredService<IPipelineApiWorkItemClient>()));
        return services;
    }

}
