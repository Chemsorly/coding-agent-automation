using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="AgentWorkerService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record AgentWorkerServiceDependencies(
    AgentConnectionLifecycle ConnectionLifecycle,
    AgentJobSlotManager SlotManager,
    ChatJobHandler ChatHandler,
    ConsolidationJobHandler ConsolidationHandler,
    IPipelineExecutor Executor,
    IJobCompletionReporter CompletionReporter,
    Serilog.ILogger Logger);
