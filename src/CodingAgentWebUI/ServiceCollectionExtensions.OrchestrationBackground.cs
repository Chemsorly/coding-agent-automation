namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers orchestration background services.
    /// All orchestration background services have moved or been deleted:
    ///   JobQueueDrainService    → deleted
    ///   AgentJobDispatcher      → deleted
    ///   HeartbeatMonitorService → deleted (Spec 041–045 arc close: ReconciliationService in JobController handles timeouts)
    /// </summary>
    private static void RegisterOrchestrationBackgroundServices(IServiceCollection services)
    {
        // Intentionally empty: all background services have been removed as part of the
        // Kubernetes-focused refactoring (Spec 041–045). The method is retained for
        // call-site clarity — future background services should be registered here.
        _ = services;
    }
}
