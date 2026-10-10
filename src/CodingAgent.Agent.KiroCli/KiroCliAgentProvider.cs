using System.Collections.Concurrent;
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
/// <remarks>
/// Sessions: the first session started in a workspace is its main conversation. <c>UseResume</c>
/// continues it by ID, never with <c>--resume</c>, which picks the newest session in the directory:
/// after an isolated reviewer, the reviewer's.
/// </remarks>
public partial class KiroCliAgentProvider : IAgentProvider
{
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly string? _model;
    private readonly AgentEffortLevel _effort;
    private readonly string _executablePath;
    private readonly string? _agentName;
    private readonly IProcessStarter _processStarter;
    private readonly Func<KiroCliLib.Configuration.Configuration, IKiroCliOrchestrator> _createEphemeralOrchestrator;
    private readonly ConcurrentDictionary<string, byte> _establishedSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _mainSessionByWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IKiroCliOrchestrator, byte> _activeOrchestrators = new();
    private int _cliSettingsApplied;
    private long _lastRunEndedTicks;

    internal const string WarmUpPrompt =
        "Briefly describe the project structure of this workspace. Do not make any changes.";

    /// <summary>Bounds the warm-up prompt, which runs outside any request timeout.</summary>
    internal static readonly TimeSpan WarmUpTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Bounds the session list, which runs after main-conversation runs.</summary>
    internal static readonly TimeSpan SessionListTimeout = TimeSpan.FromSeconds(30);

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

    public KiroCliAgentProvider(IKiroCliOrchestrator orchestrator, ILogger? logger = null, string? model = null, string executablePath = AgentDefaults.KiroCliPath, AgentEffortLevel effort = AgentEffortLevel.High, string? agentName = null)
        : this(orchestrator, logger, model, executablePath, effort, null, agentName)
    {
    }

    internal KiroCliAgentProvider(
        IKiroCliOrchestrator orchestrator, ILogger? logger, string? model, string executablePath, AgentEffortLevel effort,
        IProcessStarter? processStarter, string? agentName = null,
        Func<KiroCliLib.Configuration.Configuration, IKiroCliOrchestrator>? createEphemeralOrchestrator = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(executablePath);
        _orchestrator = orchestrator;
        _logger = logger ?? Log.Logger;
        _model = model;
        _effort = effort;
        _executablePath = executablePath;
        _agentName = agentName;
        _processStarter = processStarter ?? new DefaultProcessStarter();
        _createEphemeralOrchestrator = createEphemeralOrchestrator ?? (config => new KiroCliOrchestrator(config, _logger));
    }

    /// <summary>
    /// The run settings for this provider's orchestrators: the CLI path, and the model and agent that
    /// every run passes as flags. A blank or <c>auto</c> model and a blank agent are left out.
    /// </summary>
    public static KiroCliLib.Configuration.Configuration CreateRunConfiguration(string executablePath, string? model, string? agentName) => new()
    {
        KiroCliPath = executablePath,
        UseWsl = OperatingSystem.IsWindows(),
        Model = string.IsNullOrWhiteSpace(model) || model.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : model.Trim(),
        AgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim()
    };

    /// <inheritdoc />
    public async Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);

        if (_establishedSessions.ContainsKey(normalizedPath))
            return;

        await ApplyCliSettingsOnceAsync(ct);

        try
        {
            var exitCode = await TimeoutHelper.ExecuteWithTimeoutAsync(
                WarmUpTimeout, ct,
                linkedCt => _orchestrator.ExecutePromptAsync(WarmUpPrompt, workspacePath, useResume: false, linkedCt),
                () => Task.FromResult(ExitCodes.Timeout));

            if (exitCode != ExitCodes.Success)
            {
                _logger.Warning("Warm-up prompt exited with code {ExitCode} for workspace {WorkspacePath}; session not established",
                    exitCode, normalizedPath);
                return;
            }

            _establishedSessions[normalizedPath] = 0;
            if (!_mainSessionByWorkspace.ContainsKey(normalizedPath))
                await TrackMainSessionAsync(workspacePath, normalizedPath, replace: false, ct);
            _logger.Information("Session established via warm-up prompt for workspace {WorkspacePath}", normalizedPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Warm-up prompt failed for workspace {WorkspacePath}; session not established", normalizedPath);
        }
    }

    /// <summary>
    /// Health of the most recently active run, including isolated calls on their own orchestrators;
    /// the shared orchestrator's when nothing runs.
    /// </summary>
    public AgentHealthStatus GetHealthStatus()
    {
        // A live process wins over one that has just exited and is still being cleaned up, which
        // would otherwise read as a dead agent to every parallel call's stall monitor.
        var orchestrator = _activeOrchestrators.Keys
            .Where(o => o.IsExecuting)
            .OrderByDescending(o => o.IsActiveProcessAlive == true)
            .ThenByDescending(o => o.LastOutputTime ?? DateTime.MinValue)
            .FirstOrDefault() ?? _orchestrator;

        // Between runs, e.g. while a call reads the session list after its run, the last run's end
        // counts as output; without it the stall monitor measures from the pipeline run's start.
        var lastOutput = orchestrator.LastOutputTime;
        var lastRunEnded = Interlocked.Read(ref _lastRunEndedTicks);
        if (lastOutput is null && !orchestrator.IsExecuting && lastRunEnded != 0)
            lastOutput = new DateTime(lastRunEnded, DateTimeKind.Utc);

        return new AgentHealthStatus
        {
            IsExecuting = orchestrator.IsExecuting,
            ProcessId = orchestrator.ActiveProcessId,
            IsProcessAlive = orchestrator.IsActiveProcessAlive,
            LastOutputTime = lastOutput
        };
    }

    public async Task<AgentResult> ExecuteAsync(
        AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        await ApplyCliSettingsOnceAsync(ct);
        var normalizedPath = Path.GetFullPath(request.WorkspacePath);
        var (resumeSessionId, onMainConversation) = ResolveSession(request, normalizedPath);
        var outputLines = new List<string>();

        // For isolated (non-resume) calls, create an ephemeral orchestrator with its own
        // process slot. This enables concurrent execution — each parallel review agent gets
        // its own kiro-cli process without conflicting on the shared _orchestrator's single
        // _activeProcess field. Resume calls must use the shared orchestrator to maintain
        // session continuity.
        var isIsolatedCall = !request.UseResume && request.ResumeSessionId is null;
        var orchestrator = isIsolatedCall ? CreateEphemeralOrchestrator() : _orchestrator;
        _activeOrchestrators[orchestrator] = 0;

        try
        {
            var result = await TimeoutHelper.ExecuteWithTimeoutAsync(
                request.Timeout, ct,
                async linkedCt =>
                {
                    try
                    {
                        var exitCode = await orchestrator.ExecutePromptAsync(
                            request.Prompt,
                            request.WorkspacePath,
                            useResume: false,
                            linkedCt,
                            onOutputLine: line =>
                            {
                                var clean = AnsiStripper.Strip(line);
                                outputLines.Add(clean);
                                onOutputLine?.Invoke(clean);
                                return Task.CompletedTask;
                            },
                            resumeSessionId: resumeSessionId,
                            environmentVariables: request.EnvironmentVariables);

                        return new AgentResult { ExitCode = exitCode, OutputLines = outputLines.AsReadOnly() };
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _lastRunEndedTicks, DateTime.UtcNow.Ticks);
                    }
                },
                () =>
                {
                    _logger.Warning("Agent execution timed out after {Timeout}", request.Timeout);
                    return Task.FromResult(new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = outputLines.AsReadOnly() });
                });

            // A main-conversation run without an ID started a new session, which becomes the main one
            // unless another call got there first. One whose ID could not be loaded ran in a fresh
            // session, which replaces it. Any other run continued the known main session: the newest
            // listed session may then be an isolated call's, so the list is not asked.
            if (onMainConversation && resumeSessionId is null)
                await TrackMainSessionAsync(request.WorkspacePath, normalizedPath, replace: false, ct);
            else if (onMainConversation && orchestrator.LastRunStartedFreshSession)
                await TrackMainSessionAsync(request.WorkspacePath, normalizedPath, replace: true, ct);
            return result;
        }
        finally
        {
            _activeOrchestrators.TryRemove(orchestrator, out _);
            if (isIsolatedCall && orchestrator is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// The session a call runs in, and whether that is the workspace's main conversation. An explicit
    /// ID wins. <c>UseResume</c> continues the main conversation, or starts it when there is none; an
    /// isolated call starts it only in a workspace that has none yet.
    /// </summary>
    private (string? ResumeSessionId, bool OnMainConversation) ResolveSession(AgentRequest request, string normalizedPath)
    {
        var hasMain = _mainSessionByWorkspace.TryGetValue(normalizedPath, out var mainSessionId);
        if (request.ResumeSessionId is not null)
            return (request.ResumeSessionId, hasMain && request.ResumeSessionId == mainSessionId);
        if (request.UseResume)
            return (hasMain ? mainSessionId : null, true);
        return (null, !hasMain);
    }

    private async Task TrackMainSessionAsync(string workspacePath, string normalizedPath, bool replace, CancellationToken ct)
    {
        if (await QueryNewestSessionIdAsync(workspacePath, ct) is not { } sessionId)
            return;
        if (replace)
            _mainSessionByWorkspace[normalizedPath] = sessionId;
        else
            _mainSessionByWorkspace.TryAdd(normalizedPath, sessionId);
    }

    /// <summary>
    /// Creates a lightweight orchestrator instance with its own process slot.
    /// Used for isolated (non-resume) calls to enable parallel execution.
    /// </summary>
    private IKiroCliOrchestrator CreateEphemeralOrchestrator()
    {
        var config = CreateRunConfiguration(_executablePath, _model, _agentName);
        _logger.Debug("Creating ephemeral orchestrator (path={KiroCliPath}, wsl={UseWsl})", _executablePath, config.UseWsl);
        return _createEphemeralOrchestrator(config);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Checks the login first: <c>doctor --all --strict</c> exits 0 even when signed out, and every run
    /// would then wait about 10 minutes on a device login.
    /// </remarks>
    public async Task ValidateAsync(CancellationToken ct)
    {
        await RunCliCheckAsync("whoami", "kiro-cli is not logged in", ct);
        await RunCliCheckAsync("doctor --all --strict", "kiro-cli doctor failed", ct);
    }

    private async Task RunCliCheckAsync(string arguments, string failure, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ChildProcessEnvironment.StripTelemetry(psi);

        using var process = _processStarter.Start(psi);
        if (process is null)
        {
            _logger.Error("Failed to start kiro-cli {Arguments} process", arguments);
            throw new InvalidOperationException($"Failed to start kiro-cli {arguments} process");
        }

        string stdout, stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        finally
        {
            StopIfRunning(process);
        }

        if (process.ExitCode != 0)
        {
            var details = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            _logger.Error("kiro-cli {Arguments} exited with code {ExitCode}. {Details}", arguments, process.ExitCode, details);
            throw new InvalidOperationException(
                $"{failure}: kiro-cli {arguments} exited with code {process.ExitCode}. {details}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The workspace's main conversation when this provider tracks one; otherwise the newest session
    /// the CLI lists for the workspace.
    /// </remarks>
    public Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        return _mainSessionByWorkspace.TryGetValue(Path.GetFullPath(workspacePath), out var mainSessionId)
            ? Task.FromResult<string?>(mainSessionId)
            : QueryNewestSessionIdAsync(workspacePath, ct);
    }

    private async Task<string?> QueryNewestSessionIdAsync(string workspacePath, CancellationToken ct)
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

            string stdout;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SessionListTimeout);
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                stdout = await stdoutTask;
                await stderrTask;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.Warning("kiro-cli --list-sessions did not finish within {Timeout} for {WorkspacePath}",
                    SessionListTimeout, workspacePath);
                return null;
            }
            finally
            {
                StopIfRunning(process);
            }

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

    /// <summary>Kills a CLI check left running by a cancellation or timeout, with anything it started.</summary>
    private void StopIfRunning(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Debug(ex, "Could not stop kiro-cli process");
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
    /// <remarks>Kills the shared orchestrator's process and every isolated call's.</remarks>
    public Task KillAsync()
    {
        _orchestrator.Kill();
        foreach (var orchestrator in _activeOrchestrators.Keys)
            orchestrator.Kill();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the CLI settings on first use, for every job type: the warm-up that used to be the only
    /// writer runs for analysis only, and a fresh pod has no <c>cli.json</c>.
    /// </summary>
    private async Task ApplyCliSettingsOnceAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _cliSettingsApplied, 1) == 0)
            await ApplyCliSettingsAsync(ct);
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
