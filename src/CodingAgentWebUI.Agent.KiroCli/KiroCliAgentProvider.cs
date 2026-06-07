using CodingAgentWebUI.Agent;
using KiroCliLib.Core;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.KiroCli;

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
    private readonly AgentEffortLevel _effort;
    private readonly string _executablePath;
    private readonly IProcessStarter _processStarter;
    private readonly HashSet<string> _establishedSessions = new(StringComparer.OrdinalIgnoreCase);

    internal const string WarmUpPrompt =
        "Briefly describe the project structure of this workspace. Do not make any changes.";

    public AgentProviderType ProviderType => AgentProviderType.KiroCli;

    /// <inheritdoc />
    public bool SupportsParallelExecution => true;

    /// <inheritdoc />
    public IReadOnlyList<string> SteeringBlacklistPaths { get; } = [".kiro"];

    /// <summary>The model configured for this agent provider, or null/auto for default.</summary>
    public string? Model => _model;

    /// <summary>The effort level configured for this agent provider.</summary>
    public AgentEffortLevel Effort => _effort;

    public KiroCliAgentProvider(IKiroCliOrchestrator orchestrator, ILogger? logger = null, string? model = null, string executablePath = AgentDefaults.KiroCliPath, AgentEffortLevel effort = AgentEffortLevel.Auto)
        : this(orchestrator, logger, model, executablePath, effort, null)
    {
    }

    internal KiroCliAgentProvider(IKiroCliOrchestrator orchestrator, ILogger? logger, string? model, string executablePath, AgentEffortLevel effort, IProcessStarter? processStarter)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(executablePath);
        _orchestrator = orchestrator;
        _logger = logger ?? Log.Logger;
        _model = model;
        _effort = effort;
        _executablePath = executablePath;
        _processStarter = processStarter ?? new DefaultProcessStarter();
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

        // Set effort level before first invocation if a specific effort is configured
        await ApplyEffortSettingAsync(ct);

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

        var outputLines = new List<string>();

        // For isolated (non-resume) calls, create an ephemeral orchestrator with its own
        // process slot. This enables concurrent execution — each parallel review agent gets
        // its own kiro-cli process without conflicting on the shared _orchestrator's single
        // _activeProcess field. Resume calls must use the shared orchestrator to maintain
        // session continuity.
        var isIsolatedCall = !request.UseResume && request.ResumeSessionId is null;
        var orchestrator = isIsolatedCall ? CreateEphemeralOrchestrator() : _orchestrator;

        try
        {
            return await TimeoutHelper.ExecuteWithTimeoutAsync(
                request.Timeout, ct,
                async linkedCt =>
                {
                    var exitCode = await orchestrator.ExecutePromptAsync(
                        request.Prompt,
                        request.WorkspacePath,
                        useResume: request.UseResume,
                        linkedCt,
                        onOutputLine: line =>
                        {
                            var clean = AnsiStripper.Strip(line);
                            outputLines.Add(clean);
                            onOutputLine?.Invoke(clean);
                            return Task.CompletedTask;
                        },
                        resumeSessionId: request.ResumeSessionId);

                    return new AgentResult { ExitCode = exitCode, OutputLines = outputLines.AsReadOnly() };
                },
                () =>
                {
                    _logger.Warning("Agent execution timed out after {Timeout}", request.Timeout);
                    return Task.FromResult(new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = outputLines.AsReadOnly() });
                });
        }
        finally
        {
            if (isIsolatedCall && orchestrator is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// Creates a lightweight orchestrator instance with its own process slot.
    /// Used for isolated (non-resume) calls to enable parallel execution.
    /// </summary>
    private IKiroCliOrchestrator CreateEphemeralOrchestrator()
    {
        var config = new KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = _executablePath,
            UseWsl = OperatingSystem.IsWindows()
        };
        _logger.Debug("Creating ephemeral orchestrator (path={KiroCliPath}, wsl={UseWsl})", _executablePath, config.UseWsl);
        return new KiroCliOrchestrator(config, callbackHandler: null, _logger);
    }

    /// <inheritdoc />
    public async Task ValidateAsync(CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = "doctor --all --strict",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = _processStarter.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kiro-cli doctor process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var details = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            throw new InvalidOperationException(
                $"kiro-cli doctor exited with code {process.ExitCode}. {details}");
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetLatestSessionIdAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = "chat --list-sessions",
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = _processStarter.Start(psi);
            if (process == null) return null;

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            // Parse the first session ID from the output (most recent session listed first)
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                // Session IDs are UUIDs or similar identifiers — take the first non-empty token
                if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith("Session", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the ID portion (first whitespace-delimited token or the whole line)
                    var id = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    if (id.Length >= 8) // Minimum reasonable session ID length
                    {
                        _logger.Debug("Captured latest session ID: {SessionId}", id);
                        return id;
                    }
                }
            }

            _logger.Debug("No session ID found in --list-sessions output for {WorkspacePath}", workspacePath);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to retrieve session ID for workspace {WorkspacePath}", workspacePath);
            return null;
        }
    }

    /// <inheritdoc />
    public Task KillAsync()
    {
        _orchestrator.Kill();
        return Task.CompletedTask;
    }

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

        using var process = _processStarter.Start(psi);
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

    /// <summary>
    /// Runs <c>kiro-cli settings chat.effort "{level}"</c> if a specific (non-auto) effort is configured.
    /// </summary>
    internal async Task ApplyEffortSettingAsync(CancellationToken ct)
    {
        var cliValue = _effort.ToCliValue();
        if (cliValue is null)
            return;

        _logger.Information("Setting Kiro CLI effort to {Effort}", cliValue);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = $"settings chat.effort \"{cliValue}\"",
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = _processStarter.Start(psi);
        if (process == null)
        {
            _logger.Warning("Failed to start kiro-cli settings process for effort");
            return;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            _logger.Warning("kiro-cli settings (effort) exited with code {ExitCode}: {Error}", process.ExitCode, stderr);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
