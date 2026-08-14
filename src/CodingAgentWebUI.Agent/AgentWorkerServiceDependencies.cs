using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

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
    AgentId AgentId,
    IPipelineExecutor Executor,
    IJobCompletionReporter CompletionReporter,
    IHostApplicationLifetime HostApplicationLifetime,
    Serilog.ILogger Logger);
