using KiroCliLib.Core;

namespace CodingAgentWebUI.Services;

/// <summary>
/// No-op stub for <see cref="IKiroCliOrchestrator"/>. The orchestrator project does not
/// execute Kiro CLI locally — agents connect via SignalR from remote containers.
/// This stub satisfies the <see cref="CodingAgentWebUI.Infrastructure.ProviderFactory"/>
/// constructor contract. If <c>CreateAgentProvider</c> is ever called with a KiroCli config,
/// the returned provider will fail at execution time (which is the correct behavior for
/// the orchestrator — only agent containers should run Kiro CLI).
/// </summary>
internal sealed class NoOpKiroCliOrchestrator : IKiroCliOrchestrator
{
    public bool IsExecuting => false;
    public int? ActiveProcessId => null;
    public bool? IsActiveProcessAlive => null;
    public DateTime? LastOutputTime => null;

    public Task<int> ExecutePromptAsync(
        string prompt,
        string workspaceDirectory,
        bool useResume,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null)
    {
        throw new NotSupportedException(
            "The orchestrator does not execute Kiro CLI locally. " +
            "Agent execution is delegated to remote agent containers via SignalR.");
    }

    public void Kill() { }
}
