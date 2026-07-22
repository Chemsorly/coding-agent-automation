using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers conditional orchestration background services:
    /// HeartbeatMonitorService (skipped in Kubernetes mode) and
    /// JobQueueDrainService (hosted only in Legacy mode without a database).
    /// </summary>
    private static void RegisterOrchestrationBackgroundServices(IServiceCollection services, string? workDistributionMode)
    {
        // HeartbeatMonitorService: registered in Legacy and SignalR modes only.
        // In K8s mode, agent liveness is handled by ReconciliationService.
        var isKubernetesMode = string.Equals(workDistributionMode, "Kubernetes", StringComparison.OrdinalIgnoreCase);
        if (!isKubernetesMode)
        {
            services.AddHostedService(sp => new HeartbeatMonitorService(
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<JobDeduplicationGuardService>(),
                sp.GetRequiredService<ILabelService>(),
                sp.GetRequiredService<IConfigurationStore>(),
                Log.Logger,
                sp.GetRequiredService<IRunLifecycleManager>(),
                sp.GetService<IConsolidationService>()));
        }

        // JobQueueDrainService: registered as singleton always (AgentHubFacade depends on it),
        // but only registered as hosted service (active background loop) in Legacy mode.
        // In DB modes (SignalR/K8s), work distribution via IWorkDistributor — in-memory queue unused.
        services.AddSingleton(sp => new JobQueueDrainService(
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IJobDispatcher>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IConsolidationDispatcher>(),
            sp.GetRequiredService<IShutdownSignal>(),
            Log.Logger,
            sp.GetService<IConsolidationRunStore>()));
        var hasDatabase = !string.IsNullOrEmpty(workDistributionMode);
        if (!hasDatabase)
        {
            services.AddHostedService(sp => sp.GetRequiredService<JobQueueDrainService>());
        }
    }
}
