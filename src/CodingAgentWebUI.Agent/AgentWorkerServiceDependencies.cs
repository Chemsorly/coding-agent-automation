using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="AgentWorkerService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
// TODO: [WARNING] AgentId and HostApplicationLifetime are no longer consumed by AgentWorkerService itself —
// they are passed through to ChatJobHandler/ConsolidationJobHandler via AgentChatModeRegistration before
// being handed to this record. AgentWorkerService does not validate or store them, so callers are implicitly
// required to provide them without knowing why. Consider removing them from this record (passing them directly
// to the handler factories instead), or add ArgumentNullException.ThrowIfNull guards with an explanatory comment
// if they are retained for future use.
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
