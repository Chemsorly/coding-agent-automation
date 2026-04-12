using KiroCliLib.Core;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Agent provider that delegates to the existing KiroCliLib via IKiroCliOrchestrator.
/// Follows the same invocation pattern as KiroExecutionService.
/// The agent provider does NOT construct prompts — it receives pre-built prompts from the orchestrator.
/// </summary>
public class KiroCliAgentProvider : IAgentProvider
{
    private readonly IKiroCliOrchestrator _orchestrator;

    public AgentProviderType ProviderType => AgentProviderType.KiroCli;

    public KiroCliAgentProvider(IKiroCliOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    public async Task<AgentResult> ExecuteAsync(
        AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var outputLines = new List<string>();
        var exitCode = await _orchestrator.ExecutePromptAsync(
            request.Prompt,
            request.WorkspacePath,
            useResume: false,
            linkedCts.Token,
            onOutputLine: line => { outputLines.Add(line); onOutputLine?.Invoke(line); });

        return new AgentResult { ExitCode = exitCode, OutputLines = outputLines.AsReadOnly() };
    }

    public async Task<AgentResult> ExecuteWithResumeAsync(
        string instruction, string workspacePath, TimeSpan timeout,
        CancellationToken ct, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(workspacePath);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var outputLines = new List<string>();
        var exitCode = await _orchestrator.ExecutePromptAsync(
            instruction,
            workspacePath,
            useResume: true,
            linkedCts.Token,
            onOutputLine: line => { outputLines.Add(line); onOutputLine?.Invoke(line); });

        return new AgentResult { ExitCode = exitCode, OutputLines = outputLines.AsReadOnly() };
    }
}
