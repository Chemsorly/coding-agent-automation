namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers orchestration background services.
    /// HeartbeatMonitorService moved to CodingAgentWebUI.Api.
    /// JobQueueDrainService and AgentJobDispatcher deleted.
    /// </summary>
    private static void RegisterOrchestrationBackgroundServices(IServiceCollection services)
    {
        // All orchestration background services have moved or been deleted:
        //   HeartbeatMonitorService → CodingAgentWebUI.Api
        //   JobQueueDrainService    → deleted
        //   AgentJobDispatcher      → deleted
    }
}
