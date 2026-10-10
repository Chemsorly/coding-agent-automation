using System.Globalization;
using System.Text.Json;
using CodingAgent.Pipeline;
using KiroCliLib.Core;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.KiroCli;

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
    public string McpConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kiro", "settings", "mcp.json");

    /// <inheritdoc />
    public bool SupportsParallelExecution => true;

    /// <inheritdoc />
    public IReadOnlyList<string> PipelineInjectedPaths { get; } = [".kiro"];

    /// <inheritdoc />
    public bool SupportsVisionInput => !AgentModelCapabilities.IsTextOnlyModel(_model);

    /// <summary>The model configured for this agent provider, or null/auto for default.</summary>
    public string? Model => _model;

    /// <summary>The effort level configured for this agent provider.</summary>
    public AgentEffortLevel Effort => _effort;

    public KiroCliAgentProvider(IKiroCliOrchestrator orchestrator, ILogger? logger = null, string? model = null, string executablePath = AgentDefaults.KiroCliPath, AgentEffortLevel effort = AgentEffortLevel.High)
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
    public async Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);

        if (_establishedSessions.Contains(normalizedPath))
            return;

        // Persist model + effort settings to ~/.kiro/settings/cli.json
        await ApplyCliSettingsAsync(ct);

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
                        resumeSessionId: request.ResumeSessionId,
                        environmentVariables: request.EnvironmentVariables);

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
        return new KiroCliOrchestrator(config, _logger);
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
        ChildProcessEnvironment.StripTelemetry(psi);

        using var process = _processStarter.Start(psi);
        if (process is null)
        {
            _logger.Error("Failed to start kiro-cli doctor process");
            throw new InvalidOperationException("Failed to start kiro-cli doctor process");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var details = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            _logger.Error("kiro-cli doctor exited with code {ExitCode}. {Details}", process.ExitCode, details);
            throw new InvalidOperationException(
                $"kiro-cli doctor exited with code {process.ExitCode}. {details}");
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _executablePath,
                // Same engine as the runs, so the list holds the sessions they created. The plain
                // list goes to stderr in a display format; the JSON one goes to stdout.
                Arguments = $"chat {ProcessWrapper.AgentEngineArgument} --list-sessions --format json",
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ChildProcessEnvironment.StripTelemetry(psi);

            using var process = _processStarter.Start(psi);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            await stderrTask;

            var id = ParseLatestSessionId(stdout);
            if (id is not null)
                _logger.Debug("Captured latest session ID: {SessionId}", id);
            else
                _logger.Debug("No session ID found in --list-sessions output for {WorkspacePath}", workspacePath);
            return id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to retrieve session ID for workspace {WorkspacePath}", workspacePath);
            return null;
        }
    }

    /// <summary>
    /// The newest session in <c>kiro-cli chat --list-sessions --format json</c> output, which lists
    /// sessions newest first: <c>[{"cwd": "…", "sessions": [{"sessionId": "…", "updatedAt": "…"}]}]</c>.
    /// A later entry wins only with a newer <c>updatedAt</c>. Null when no session is listed or the
    /// output is not that JSON.
    /// </summary>
    internal static string? ParseLatestSessionId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            string? latestId = null;
            DateTimeOffset? latestUpdatedAt = null;
            foreach (var session in document.RootElement.EnumerateArray().SelectMany(ListedSessions))
            {
                if (!TryReadSession(session, out var id, out var updatedAt))
                    continue;
                if (latestId is null || updatedAt > latestUpdatedAt)
                {
                    latestId = id;
                    latestUpdatedAt = updatedAt;
                }
            }
            return latestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> ListedSessions(JsonElement envelope) =>
        envelope.ValueKind == JsonValueKind.Object
        && envelope.TryGetProperty("sessions", out var sessions)
        && sessions.ValueKind == JsonValueKind.Array
            ? sessions.EnumerateArray()
            : [];

    private static bool TryReadSession(JsonElement session, out string id, out DateTimeOffset? updatedAt)
    {
        id = string.Empty;
        updatedAt = null;
        if (session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("sessionId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
            return false;

        id = idElement.GetString()!;
        if (session.TryGetProperty("updatedAt", out var updatedElement)
            && updatedElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            updatedAt = parsed;
        return true;
    }

    /// <inheritdoc />
    public Task KillAsync()
    {
        _orchestrator.Kill();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Persists model and effort settings to <c>~/.kiro/settings/cli.json</c>.
    /// Delegates to <see cref="KiroCliSettingsWriter.ApplyAsync"/> which handles
    /// model-name validation, read-merge-write semantics, and effort gating.
    /// </summary>
    internal async Task ApplyCliSettingsAsync(CancellationToken ct, string? settingsPathOverride = null)
    {
        var hasModel = !string.IsNullOrEmpty(_model) && !_model.Equals("auto", StringComparison.OrdinalIgnoreCase);

        // Guard against passing null to the non-nullable model parameter.
        // KiroCliSettingsWriter.ApplyAsync also short-circuits on null/auto, but this
        // avoids a nullable coercion when _model is null.
        if (!hasModel)
            return;

        // TODO: The rejection warning for invalid model names is now emitted via the static
        // Serilog.Log sink (inside KiroCliSettingsWriter) rather than through the injected
        // _logger. Consumers that wrap _logger with custom enrichers or sinks will silently
        // miss these rejection events. If scoped-logger visibility is required, intercept
        // the result or duplicate the validation check here. See review warning (issue #2346).
        await KiroCliSettingsWriter.ApplyAsync(_model!, _effort.ToCliValue(), ct, settingsPathOverride);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
