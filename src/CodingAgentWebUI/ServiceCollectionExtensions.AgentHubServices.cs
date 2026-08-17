using CodingAgentWebUI.Hubs;
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
    {
        services.AddSingleton<AgentHubFacadeDependencies>(sp => new AgentHubFacadeDependencies(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<JobQueueDrainService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILogger<AgentHubFacadeDependencies>>(),
            sp.GetService<WorkItemTransitionService>(),
            sp.GetService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetService<IWorkItemFallbackTransitionService>()));

        services.AddSingleton<IAgentHubFacade>(sp => new AgentHubFacade(
            sp.GetRequiredService<AgentHubFacadeDependencies>()));

        services.AddSingleton<IHubIssueOperations>(sp => new AgentIssueOperations(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<ILabelService>(),
            Log.Logger));

        services.AddSingleton<IAgentOrphanRecoveryService>(sp => new AgentOrphanRecoveryService(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<IChangeNotifier>(),
            Log.Logger));

        services.AddSingleton<IAgentJobLifecycleService>(sp => new AgentJobLifecycleService(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<IRunLifecycleManager>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<IHubIssueOperations>(),
            sp.GetRequiredService<IChangeNotifier>(),
            Log.Logger));

        services.AddSingleton<IAgentTokenRefreshService>(sp => new AgentTokenRefreshService(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<ITokenVendingService>(),
            Log.Logger));

        services.AddSingleton<IGateCommentFormatter>(sp => new GateCommentFormatter(
            Log.Logger));

        // AgentHubDependencies is scoped to match the Hub's per-connection lifetime.
        // All 12 wrapped dependencies are singletons, so this is a safe downgrade.
        services.AddScoped(sp => new AgentHubDependencies(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<IChatNotifier>(),
            sp.GetRequiredService<IChangeNotifier>(),
            sp.GetRequiredService<ModelFetchService>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<ConsolidationBadgeService>(),
            sp.GetRequiredService<IHubIssueOperations>(),
            sp.GetRequiredService<IAgentJobLifecycleService>(),
            sp.GetRequiredService<IAgentTokenRefreshService>(),
            sp.GetRequiredService<IGateCommentFormatter>(),
            Log.Logger,
            sp.GetRequiredService<IAgentOrphanRecoveryService>()));
    }
}
