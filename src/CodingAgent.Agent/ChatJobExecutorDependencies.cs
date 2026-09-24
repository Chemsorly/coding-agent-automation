using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="ChatJobExecutor"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record ChatJobExecutorDependencies(
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

    /// <summary>
    /// Delegate used to start a child process in <see cref="ChatJobExecutor.HandleFetchModelsAsync"/>.
    /// Defaults to <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
    /// Override in tests to capture the <see cref="System.Diagnostics.ProcessStartInfo"/> passed to the
    /// process starter and assert that OTEL environment variables have been stripped.
    /// </summary>
    public Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> ProcessStarter { get; init; }
        = System.Diagnostics.Process.Start;
}
