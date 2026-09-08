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
    Serilog.ILogger Logger)
{
    /// <summary>
    /// Grace period to wait for the in-flight chat task to finish after CancelChat before giving up.
    /// Defaults to the production 10s; tests set a small value so a deliberately-hanging chat task
    /// does not force a real 10s wait.
    /// </summary>
    public TimeSpan ChatTaskCompletionGracePeriod { get; init; } = TimeSpan.FromSeconds(10);
}
