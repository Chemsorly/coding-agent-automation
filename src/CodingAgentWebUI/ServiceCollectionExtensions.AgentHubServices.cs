using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="AgentMonitoringPageServiceDependencies"/> record needed
    /// by <see cref="AgentMonitoringPageService"/> (which is registered as scoped via AddScoped&lt;T&gt;
    /// auto-construction in Program.cs).
    /// <para>
    /// Spec 044: IOrchestratorRunService, IRunLifecycleManager, PipelineRunLifecycleService, and
    /// IHubContext&lt;AgentHub&gt; removed — the monolith is in degraded (history-only) mode.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAgentMonitoringPageServiceDependencies(this IServiceCollection services)
    {
        services.AddScoped(sp => new AgentMonitoringPageServiceDependencies(
            sp.GetRequiredService<IActiveRunQueryService>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IPendingWorkQuery>(),
            sp.GetRequiredService<IWorkDistributor>(),
            sp.GetRequiredService<IPipelineRunHistoryService>()));
        return services;
    }

}
