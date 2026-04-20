using KiroCliLib.Core;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Agent provider that delegates to the existing KiroCliLib via IKiroCliOrchestrator.
/// Follows the same invocation pattern as KiroExecutionService.
/// The agent provider does NOT construct prompts — it receives pre-built prompts from the orchestrator.
/// </summary>
public class KiroCliAgentProvider : IAgentProvider
{
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly HashSet<string> _establishedSessions = new(StringComparer.OrdinalIgnoreCase);

    internal const string WarmUpPrompt =
        "Briefly describe the project structure of this workspace. Do not make any changes.";

    public AgentProviderType ProviderType => AgentProviderType.KiroCli;

    public KiroCliAgentProvider(IKiroCliOrchestrator orchestrator, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
        _logger = logger ?? Log.Logger;
    }

    /// <inheritdoc />
    public async Task EnsureSessionAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        var normalizedPath = Path.GetFullPath(workspacePath);

        if (_establishedSessions.Contains(normalizedPath))
            return;

        try
        {
            await _orchestrator.ExecutePromptAsync(
                WarmUpPrompt,
                workspacePath,
                useResume: false,
                ct);

            _establishedSessions.Add(normalizedPath);
            _logger.Information("Session established via warm-up prompt for workspace {WorkspacePath}", normalizedPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Warm-up prompt failed for workspace {WorkspacePath}; session not established", normalizedPath);
        }
    }

    public AgentHealthStatus GetHealthStatus() => new()
    {
        IsExecuting = _orchestrator.IsExecuting,
        ProcessId = _orchestrator.ActiveProcessId,
        IsProcessAlive = _orchestrator.IsActiveProcessAlive,
        LastOutputTime = _orchestrator.LastOutputTime
    };

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
            useResume: request.UseResume,
            linkedCts.Token,
            onOutputLine: line =>
            {
                var clean = AnsiStripper.Strip(line);
                outputLines.Add(clean);
                onOutputLine?.Invoke(clean);
            });

        return new AgentResult { ExitCode = exitCode, OutputLines = outputLines.AsReadOnly() };
    }

    /// <inheritdoc />
    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
