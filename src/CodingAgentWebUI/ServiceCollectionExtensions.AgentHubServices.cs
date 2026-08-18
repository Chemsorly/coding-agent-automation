using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="AgentMonitoringPageServiceDependencies"/> record needed
    /// by <see cref="AgentMonitoringPageService"/> (which is registered as scoped via AddScoped&lt;T&gt;
    /// auto-construction in Program.cs).
    /// </summary>
    public static IServiceCollection AddAgentMonitoringPageServiceDependencies(this IServiceCollection services)
    {
        services.AddScoped(sp => new AgentMonitoringPageServiceDependencies(
            sp.GetRequiredService<IActiveRunQueryService>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<PipelineRunLifecycleService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IPendingWorkQuery>(),
            sp.GetRequiredService<IWorkDistributor>(),
            sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IRunLifecycleManager>()));
        return services;
    }

    /// <summary>
    /// Registers the AgentHub facade and hub-dependent services:
    /// hub facade, issue operations, orphan recovery, and job lifecycle.
    /// </summary>
    private static void RegisterAgentHubServices(IServiceCollection services)
        => services.AddAgentHubServices();
}
