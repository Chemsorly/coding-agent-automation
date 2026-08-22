using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodingAgentWebUI.Hub;

public static class AgentHubServiceCollectionExtensions
{
    public static IServiceCollection AddAgentHubServices(this IServiceCollection services)
    {
        services.AddSingleton<AgentHubFacadeDependencies>(sp => new AgentHubFacadeDependencies(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILogger<AgentHubFacadeDependencies>>(),
            sp.GetService<WorkItemTransitionService>(),
            sp.GetService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetService<IProjectStore>(),
            sp.GetService<IWorkItemFallbackTransitionService>(),
            sp.GetRequiredService<TimeProvider>()));

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

        // ── IHubConsolidationOperations (T10: extracted from AgentHub) ──────────────
        services.AddSingleton<IHubConsolidationOperations>(sp => new HubConsolidationOperations(
            sp.GetRequiredService<ModelFetchService>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<ConsolidationBadgeService>(),
            sp.GetRequiredService<IChangeNotifier>(),
            Log.Logger));

        // AgentHubDependencies is scoped to match the Hub's per-connection lifetime.
        // All wrapped dependencies are singletons, so this is a safe downgrade.
        // T10: 13 → 10 members — consolidation cluster extracted into IHubConsolidationOperations.
        services.AddScoped(sp => new AgentHubDependencies(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<IChatNotifier>(),
            sp.GetRequiredService<IChangeNotifier>(),
            sp.GetRequiredService<IHubConsolidationOperations>(),
            sp.GetRequiredService<IHubIssueOperations>(),
            sp.GetRequiredService<IAgentJobLifecycleService>(),
            sp.GetRequiredService<IAgentTokenRefreshService>(),
            sp.GetRequiredService<IGateCommentFormatter>(),
            Log.Logger,
            sp.GetRequiredService<IAgentOrphanRecoveryService>(),
            sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<AgentHub>>()));

        return services;
    }
}
