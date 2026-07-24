using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AgentHub facade and hub-dependent services:
    /// hub facade, issue operations, orphan recovery, and job lifecycle.
    /// </summary>
    private static void RegisterAgentHubServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentHubFacade>(sp => new AgentHubFacade(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<JobQueueDrainService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILogger<AgentHubFacade>>(),
            sp.GetService<WorkItemTransitionService>(),
            sp.GetService<PendingWorkItemDrainService>(),
            sp.GetService<IDbContextFactory<PipelineDbContext>>()));

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
    }
}
