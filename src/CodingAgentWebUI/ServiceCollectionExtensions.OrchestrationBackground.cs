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
    /// Registers orchestration background services:
    /// HeartbeatMonitorService (unconditional — chat pods require it) and
    /// JobQueueDrainService singleton (AgentHubFacade dep; not hosted — Legacy dispatch is removed).
    /// </summary>
    private static void RegisterOrchestrationBackgroundServices(IServiceCollection services)
    {
        // HeartbeatMonitorService: registered unconditionally.
        // Chat pods use the agent-side SignalR stack and register/heartbeat over the hub;
        // without this monitor, dead chat pods linger in the registry.
        services.AddHostedService(sp => new HeartbeatMonitorService(
            new HeartbeatMonitorDependencies(
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IConfigurationStore>(),
                Log.Logger,
                sp.GetRequiredService<IRunLifecycleManager>(),
                sp.GetService<IConsolidationService>())));

        // JobQueueDrainService: registered as singleton (AgentHubFacade depends on it),
        // but NOT as a hosted service — Legacy in-memory queue dispatch is removed.
        // TODO(Spec 043/044, same branch): dead after 041 — no Legacy/SignalR dispatch remains. Removed when the hub moves out of the monolith.
        services.AddSingleton(sp => new JobQueueDrainService(
            new JobQueueDrainDependencies(
                sp.GetRequiredService<JobDeduplicationGuardService>(),
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<IJobDispatcher>(),
                sp.GetRequiredService<IConfigurationStore>(),
                sp.GetRequiredService<IConsolidationDispatchService>(),
                sp.GetRequiredService<IShutdownSignal>(),
                Log.Logger,
                sp.GetService<IConsolidationRunStore>())));
    }
}
