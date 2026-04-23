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
public partial class KiroCliAgentProvider : IAgentProvider
{
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly string? _model;
    private readonly string _executablePath;
    private readonly HashSet<string> _establishedSessions = new(StringComparer.OrdinalIgnoreCase);

    internal const string WarmUpPrompt =
        "Briefly describe the project structure of this workspace. Do not make any changes.";

    public AgentProviderType ProviderType => AgentProviderType.KiroCli;

    /// <summary>The model configured for this agent provider, or null/auto for default.</summary>
    public string? Model => _model;

    public KiroCliAgentProvider(IKiroCliOrchestrator orchestrator, ILogger? logger = null, string? model = null, string executablePath = "/home/ubuntu/.local/bin/kiro-cli")
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
        _logger = logger ?? Log.Logger;
        _model = model;
        _executablePath = executablePath;
    }

    /// <inheritdoc />
    public async Task EnsureSessionAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        var normalizedPath = Path.GetFullPath(workspacePath);

        if (_establishedSessions.Contains(normalizedPath))
            return;

        // Set model before first invocation if a specific model is configured
        await ApplyModelSettingAsync(ct);

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
        int exitCode;
        try
        {
            exitCode = await _orchestrator.ExecutePromptAsync(
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
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The timeout CTS fired, not the user's cancellation token.
            // Convert to exit code 124 (timeout) so the orchestrator can distinguish
            // timeout from user cancellation.
            _logger.Warning("Agent execution timed out after {Timeout}", request.Timeout);
            exitCode = 124;
        }

        return new AgentResult { ExitCode = exitCode, OutputLines = outputLines.AsReadOnly() };
    }

    /// <inheritdoc />
    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Runs <c>kiro-cli settings chat.defaultModel "model"</c> if a specific (non-auto) model is configured.
    /// </summary>
    internal async Task ApplyModelSettingAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_model) || _model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return;

        if (!ModelNamePattern().IsMatch(_model))
        {
            _logger.Warning("Invalid model name rejected: {Model}", _model);
            return;
        }

        _logger.Information("Setting Kiro CLI model to {Model}", _model);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = $"settings chat.defaultModel \"{_model}\"",
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            _logger.Warning("Failed to start kiro-cli settings process");
            return;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            _logger.Warning("kiro-cli settings exited with code {ExitCode}: {Error}", process.ExitCode, stderr);
    }

    /// <summary>Pattern for valid model names: alphanumeric, dots, hyphens, underscores.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial System.Text.RegularExpressions.Regex ModelNamePattern();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
