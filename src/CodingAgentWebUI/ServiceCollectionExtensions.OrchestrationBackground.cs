namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers orchestration background services.
    /// HeartbeatMonitorService moved to CodingAgentWebUI.Api in Spec 044 Task 6 (Req 3.7a).
    /// JobQueueDrainService removed — dead after 041 (no Legacy/SignalR dispatch remains).
    /// </summary>
    private static void RegisterOrchestrationBackgroundServices(IServiceCollection services)
    {
        // All background services that depended on IOrchestratorRunService and IRunLifecycleManager
        // have been removed here:
        //   HeartbeatMonitorService → registered in CodingAgentWebUI.Api Program.cs (Req 3.7a, 3.7b)
        //   JobQueueDrainService    → deleted in Spec 044 Task 15d (no Legacy dispatch after 041)
        //   AgentJobDispatcher      → deleted in Spec 044 Task 15d (source files removed)
    }
}
