using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers Legacy mode services — no database, JSON-based config store,
    /// in-memory work distribution via AgentJobDispatcher.
    /// IConfigurationStore is already registered via AddInfrastructureServices.
    /// </summary>
    private static void RegisterLegacyMode(IServiceCollection services)
    {
        // IWorkDistributor wraps existing AgentJobDispatcher.
        services.AddSingleton<IWorkDistributor>(sp => new LegacyWorkDistributor(
            sp.GetRequiredService<IJobDispatcher>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger,
            new Lazy<IConsolidationDispatcher>(() => sp.GetRequiredService<IConsolidationDispatcher>())));
        services.AddSingleton<IActiveRunQueryService>(sp => new InMemoryActiveRunQueryService(
            sp.GetRequiredService<IOrchestratorRunService>()));
        services.AddSingleton<IConsolidationRunStore>(sp =>
            new FileSystemConsolidationRunStore(
                PipelineConstants.ConsolidationRunsDirectory));
        services.AddSingleton<ILoopStateStore>(sp =>
            new FileSystemLoopStateStore(
                Path.Combine(PipelineConstants.ConfigBaseDirectory, "loop-state.json")));
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new FileSystemHarnessSuggestionStore(
                PipelineConstants.HarnessSuggestionsPath));
        services.AddDistributedLockProvider(null);
        // Queue visibility: wraps in-memory JobDeduplicationGuardService
        services.AddSingleton<IPendingWorkQuery>(sp =>
            new LegacyPendingWorkQuery(sp.GetRequiredService<JobDeduplicationGuardService>()));
        // RunLifecycleManager (Legacy — no WorkItemTransitionService, no K8s cleanup)
        services.AddSingleton<IJobCleanupStrategy>(new NoOpJobCleanup());
        services.AddSingleton<IRunLifecycleManager>(sp => new Orchestration.RunLifecycleManager(
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            Log.Logger,
            workItemTransition: null,
            jobCleanup: sp.GetRequiredService<IJobCleanupStrategy>()));
        Log.Information("WorkDistribution: Legacy mode (no database). Using JsonConfigurationStore + LegacyWorkDistributor");
    }
}
