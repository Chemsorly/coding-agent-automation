namespace CodingAgent.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="AgentWorkerService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record AgentWorkerServiceDependencies(
    AgentConnectionLifecycle ConnectionLifecycle,
    ChatSlotManager SlotManager,
    ChatJobExecutor ChatHandler,
    Serilog.ILogger Logger);
