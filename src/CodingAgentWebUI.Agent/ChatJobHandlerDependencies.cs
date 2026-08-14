using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="ChatJobHandler"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record ChatJobHandlerDependencies(
    AgentConnectionLifecycle ConnectionLifecycle,
    AgentJobSlotManager SlotManager,
    IKiroCliOrchestrator Orchestrator,
    System.Net.Http.IHttpClientFactory HttpClientFactory,
    IHostApplicationLifetime HostApplicationLifetime,
    Func<Task> SignalAgentReady,
    bool IsOpenCodeProvider,
    bool IsChatMode,
    Serilog.ILogger Logger);
