using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using KiroCliLib.Core;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.ClaudeCode;

/// <summary>How the Claude Code CLI is invoked: the agent provider config's settings.</summary>
/// <param name="Model">Model ID or alias; null or "auto" leaves the choice to the CLI.</param>
/// <param name="ExecutablePath">Path to the <c>claude</c> binary.</param>
/// <param name="Effort">Passed as <c>--effort</c> unless <see cref="AgentEffortLevel.Auto"/>.</param>
/// <param name="AuthMode">One of <see cref="ClaudeCodeAuthModes"/>; anything else means auto.</param>
/// <param name="McpConfigPath">MCP config file passed with <c>--mcp-config</c>; null means <c>~/.claude/pipeline-mcp.json</c>.</param>
internal sealed record ClaudeCodeSettings(
    string? Model,
    string ExecutablePath,
    AgentEffortLevel Effort,
    string? AuthMode,
    string? McpConfigPath);

/// <summary>
/// Agent provider that runs the Claude Code CLI headless (<c>claude -p --output-format stream-json</c>),
/// one process per call. The provider does not build prompts; it receives them from the pipeline.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The prompt goes in on stdin, so no argument escaping or length limit applies.</item>
/// <item>Sessions follow OpenCode's rules: a fresh call starts a session and becomes the workspace's
/// session; <c>UseResume</c> continues it with <c>--resume</c>; <c>ResumeSessionId</c> targets one.</item>
/// <item>Exactly one credential reaches the CLI; see <see cref="ClaudeCodeCredentials"/>.</item>
/// <item>A CLI still running well after its <c>result</c> event is stopped; see <see cref="DefaultResultExitGrace"/>.</item>
/// <item>Steering is not written here: the pipeline puts it in the user rules directory
/// (<c>~/.claude/rules/</c>), which the CLI loads in every project, outside the workspace.</item>
/// </list>
/// </remarks>
public sealed class ClaudeCodeAgentProvider : IAgentProvider
{
    internal const string ProviderTag = "claude";

    private const string ImagesNote = "Images attached to this task (open them with the Read tool):";
    private static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the CLI may keep running after its <c>result</c> event before it is stopped. Since CLI
    /// 2.1.292, <c>claude -p</c> waits for background commands the agent left running (a dev server, a
    /// watcher) until they end or reach their own limit (30 minutes by default); before, it stopped them
    /// 5 seconds after the result. The grace leaves the CLI time to save the session for <c>--resume</c>.
    /// </summary>
    internal static readonly TimeSpan DefaultResultExitGrace = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a CLI stopped after its result to exit and deliver its last lines.</summary>
    private static readonly TimeSpan StoppedExitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly string? _model;
    private readonly AgentEffortLevel _effort;
    private readonly string _executablePath;
    private readonly string _authMode;
    private readonly string _mcpConfigPath;
    private readonly IClaudeProcessLauncher _launcher;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly TimeSpan _resultExitGrace;

    private readonly ConcurrentDictionary<IClaudeProcess, byte> _activeProcesses = new();
    private readonly ConcurrentDictionary<string, string> _sessionByWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ClaudeUsageTotals> _totalsBySession = new(StringComparer.Ordinal);
    private volatile string? _lastKnownSessionId;

    public ClaudeCodeAgentProvider(
        ILogger? logger = null,
        string? model = null,
        string executablePath = AgentDefaults.ClaudeCliPath,
        AgentEffortLevel effort = AgentEffortLevel.Auto,
        string? authMode = null,
        string? mcpConfigPath = null)
        : this(logger, new ClaudeCodeSettings(model, executablePath, effort, authMode, mcpConfigPath), null, null)
    {
    }

    internal ClaudeCodeAgentProvider(
        ILogger? logger,
        ClaudeCodeSettings settings,
        IClaudeProcessLauncher? launcher,
        Func<string, string?>? getEnvironmentVariable,
        TimeSpan? resultExitGrace = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ExecutablePath);
        _logger = logger ?? Log.Logger;
        _model = settings.Model;
        _effort = settings.Effort;
        _executablePath = settings.ExecutablePath;
        _authMode = ClaudeCodeAuthModes.Normalize(settings.AuthMode);
        _mcpConfigPath = string.IsNullOrWhiteSpace(settings.McpConfigPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "pipeline-mcp.json")
            : settings.McpConfigPath;
        _launcher = launcher ?? SystemClaudeProcessLauncher.Instance;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _resultExitGrace = resultExitGrace ?? DefaultResultExitGrace;
    }

    public AgentProviderType ProviderType => AgentProviderType.ClaudeCode;

    /// <inheritdoc />
    public string? Model => _model;

    /// <summary>The effort level passed to the CLI with <c>--effort</c>.</summary>
    public AgentEffortLevel Effort => _effort;

    /// <summary>The configured credential mode (<see cref="ClaudeCodeAuthModes"/>).</summary>
    public string AuthMode => _authMode;

    /// <inheritdoc />
    /// <remarks>Passed to the CLI with <c>--mcp-config</c> when the file exists.</remarks>
    public string McpConfigPath => _mcpConfigPath;

    /// <inheritdoc />
    public bool SupportsParallelExecution => true;

    /// <inheritdoc />
    /// <remarks>
    /// The pipeline writes nothing into the workspace for Claude Code. These are the CLI's personal,
    /// never-committed files, listed so an agent can never commit them.
    /// </remarks>
    public IReadOnlyList<string> PipelineInjectedPaths { get; } = ["CLAUDE.local.md", ".claude/settings.local.json"];

    /// <inheritdoc />
    public bool SupportsVisionInput => true;

    /// <inheritdoc />
    /// <remarks>No-op: the CLI answers the first prompt of a session, so no warm-up is needed.</remarks>
    public Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.CompletedTask;

    public AgentHealthStatus GetHealthStatus()
    {
        var latest = _activeProcesses.Keys.OrderByDescending(p => p.LastOutputTime).FirstOrDefault();
        return new AgentHealthStatus
        {
            IsExecuting = latest is not null,
            ProcessId = latest?.ProcessId,
            IsProcessAlive = latest?.IsRunning,
            LastOutputTime = latest?.LastOutputTime
        };
    }

    public async Task<AgentResult> ExecuteAsync(
        AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credential = ClaudeCodeCredentials.Resolve(_authMode, _getEnvironmentVariable);
        if (credential is null && _authMode != ClaudeCodeAuthModes.Auto)
            return MissingCredentialResult();

        var workspacePath = Path.GetFullPath(request.WorkspacePath);
        var resumeSessionId = ResolveResumeSessionId(request, workspacePath);
        var state = new ClaudeStreamState();
        var outputLines = new List<string>();
        var stderrLines = new List<string>();

        void Emit(string line)
        {
            lock (outputLines)
                outputLines.Add(line);
            onOutputLine?.Invoke(line);
        }

        var startInfo = BuildStartInfo(request, workspacePath, resumeSessionId, credential);
        var stopwatch = Stopwatch.StartNew();

        var exitCode = await TimeoutHelper.ExecuteWithTimeoutAsync(
            request.Timeout, ct,
            linkedCt => RunProcessAsync(startInfo, BuildPrompt(request), state, Emit, stderrLines, linkedCt),
            () =>
            {
                _logger.Warning("Claude Code execution timed out after {Timeout}", request.Timeout);
                return Task.FromResult(ExitCodes.Timeout);
            });

        if (exitCode == ExitCodes.Success && state.ResultIsError)
            exitCode = ExitCodes.GeneralFailure;

        if (exitCode != ExitCodes.Success && !state.ResultIsError)
        {
            // Without a result event the reason is on stderr (bad flag, missing binary, crash).
            foreach (var line in stderrLines.TakeLast(5))
                Emit(line);
        }

        RememberSession(state.SessionId, workspacePath, resumeSessionId is null);
        var details = BuildUsage(state, credential, resumeSessionId is not null, out var usage, out var cost);
        var errorCategory = exitCode == ExitCodes.Success ? AgentErrorCategory.None : state.ClassifyFailure();

        LogInvocation(state, exitCode, stopwatch.Elapsed, usage, cost, details, errorCategory);

        IReadOnlyList<string> lines;
        lock (outputLines)
            lines = outputLines.ToList().AsReadOnly();

        return new AgentResult
        {
            ExitCode = exitCode,
            OutputLines = lines,
            Usage = usage,
            Cost = cost,
            UsageDetails = details,
            ErrorCategory = errorCategory
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Checks that the selected credential is configured and that the CLI starts (<c>claude --version</c>).
    /// Makes no model call, so validation costs nothing.
    /// </remarks>
    public async Task ValidateAsync(CancellationToken ct)
    {
        if (_authMode != ClaudeCodeAuthModes.Auto && ClaudeCodeCredentials.Resolve(_authMode, _getEnvironmentVariable) is null)
        {
            _logger.Error("Claude Code auth mode {AuthMode} is selected but its credential is not configured", _authMode);
            throw new InvalidOperationException(MissingCredentialMessage());
        }

        var startInfo = CreateStartInfo(Environment.CurrentDirectory);
        startInfo.ArgumentList.Add("--version");

        var output = new List<string>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ValidateTimeout);

        using var process = _launcher.Start(startInfo, line => { lock (output) output.Add(line); }, line => { lock (output) output.Add(line); });
        try
        {
            await process.WriteStdinAndCloseAsync(string.Empty, timeoutCts.Token);
        }
        catch (IOException)
        {
            // The CLI exited before reading stdin; its exit code below tells whether it works.
        }
        var exitCode = await process.WaitForExitAsync(timeoutCts.Token);
        if (exitCode != ExitCodes.Success)
        {
            var details = string.Join(" ", output).Trim();
            _logger.Error("claude --version exited with code {ExitCode}. {Details}", exitCode, details);
            throw new InvalidOperationException($"claude --version exited with code {exitCode}. {details}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The latest session started by a fresh call, as reported by the CLI. No process is started.
    /// </remarks>
    public Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);
        return Task.FromResult(_sessionByWorkspace.TryGetValue(normalizedPath, out var sessionId)
            ? sessionId
            : _lastKnownSessionId);
    }

    /// <inheritdoc />
    public Task KillAsync()
    {
        foreach (var process in _activeProcesses.Keys)
            process.Kill();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        foreach (var process in _activeProcesses.Keys)
            process.Kill();
        _sessionByWorkspace.Clear();
        _totalsBySession.Clear();
        _lastKnownSessionId = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds the CLI arguments. The prompt is not among them; it goes in on stdin.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        string? model, AgentEffortLevel effort, string? resumeSessionId, string? mcpConfigPath)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose", // required by stream-json in print mode
            // Same trust level as kiro-cli --trust-all-tools: the pod is the sandbox.
            "--dangerously-skip-permissions"
        };

        if (!string.IsNullOrWhiteSpace(model) && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["--model", model]);

        if (effort.ToCliValue() is { } effortValue)
            args.AddRange(["--effort", effortValue]);

        if (resumeSessionId is not null)
            args.AddRange(["--resume", resumeSessionId]);

        // Last: --mcp-config takes several values. Repo .mcp.json servers still load next to these.
        if (mcpConfigPath is not null)
            args.AddRange(["--mcp-config", mcpConfigPath]);

        return args;
    }

    private string? ResolveResumeSessionId(AgentRequest request, string workspacePath)
    {
        if (!string.IsNullOrEmpty(request.ResumeSessionId))
            return request.ResumeSessionId;

        return request.UseResume && _sessionByWorkspace.TryGetValue(workspacePath, out var sessionId)
            ? sessionId
            : null;
    }

    private void RememberSession(string? sessionId, string workspacePath, bool isFreshSession)
    {
        if (sessionId is null || !isFreshSession)
            return;
        _sessionByWorkspace[workspacePath] = sessionId;
        _lastKnownSessionId = sessionId;
    }

    private ProcessStartInfo BuildStartInfo(
        AgentRequest request, string workspacePath, string? resumeSessionId, ClaudeCredential? credential)
    {
        var startInfo = CreateStartInfo(workspacePath);
        var mcpConfigPath = File.Exists(_mcpConfigPath) ? _mcpConfigPath : null;
        foreach (var arg in BuildArguments(_model, _effort, resumeSessionId, mcpConfigPath))
            startInfo.ArgumentList.Add(arg);

        if (request.EnvironmentVariables is { Count: > 0 } secrets)
        {
            foreach (var (key, value) in secrets)
            {
                if (ClaudeCodeCredentials.IsCredentialVariable(key))
                {
                    // A project secret must not decide which account the agent runs on.
                    _logger.Warning("Project secret {SecretName} is not passed to Claude Code: it would replace the agent's credential", key);
                    continue;
                }
                startInfo.Environment[key] = value;
            }
        }

        ClaudeCodeCredentials.Apply(startInfo.Environment, credential);
        return startInfo;
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Removes OTEL settings (the export token) and the pipeline's AGENT_CLAUDE_* credentials.
        ChildProcessEnvironment.StripTelemetry(startInfo);
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        // Pods are thrown away after the run, and the pipeline has its own memory (the brain).
        startInfo.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        return startInfo;
    }

    private static string BuildPrompt(AgentRequest request)
    {
        if (request.ImagePaths is not { Count: > 0 } images)
            return request.Prompt;

        var prompt = new StringBuilder(request.Prompt)
            .AppendLine()
            .AppendLine()
            .AppendLine(ImagesNote);
        foreach (var image in images)
            prompt.Append("- ").AppendLine(image);
        return prompt.ToString();
    }

    private async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        string prompt,
        ClaudeStreamState state,
        Action<string> emit,
        List<string> stderrLines,
        CancellationToken ct)
    {
        IClaudeProcess process;
        var resultSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            process = _launcher.Start(
                startInfo,
                line =>
                {
                    foreach (var readable in ClaudeStreamJsonParser.ProcessLine(line, state))
                        emit(readable);
                    if (state.ResultSeen)
                        resultSeen.TrySetResult();
                },
                line =>
                {
                    _logger.Debug("Claude (stderr): {Line}", line);
                    lock (stderrLines)
                        stderrLines.Add(AnsiStripper.Strip(line));
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Failed to start Claude Code CLI at {ExecutablePath}", _executablePath);
            emit($"Failed to start Claude Code CLI at {_executablePath}: {ex.Message}");
            return ExitCodes.GeneralFailure;
        }

        _activeProcesses[process] = 0;
        try
        {
            try
            {
                await process.WriteStdinAndCloseAsync(prompt, ct);
            }
            catch (IOException ex)
            {
                // The CLI exited before reading stdin (e.g. a bad flag); its exit code tells why.
                _logger.Warning(ex, "Claude Code CLI closed stdin before the prompt was written");
            }

            return await WaitForExitOrStopAfterResultAsync(process, resultSeen.Task, ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
        finally
        {
            _activeProcesses.TryRemove(process, out _);
            process.Dispose();
        }
    }

    /// <summary>
    /// Waits for the CLI to exit, or stops it when it is still running <see cref="_resultExitGrace"/>
    /// after its <c>result</c> event (see <see cref="DefaultResultExitGrace"/>). The answer is complete at
    /// the result, so a stopped CLI counts as a success; an error result still fails the call.
    /// </summary>
    private async Task<int> WaitForExitOrStopAfterResultAsync(IClaudeProcess process, Task resultSeen, CancellationToken ct)
    {
        var exit = process.WaitForExitAsync(ct);
        if (await Task.WhenAny(exit, resultSeen) == exit)
            return await exit;

        if (await Task.WhenAny(exit, Task.Delay(_resultExitGrace, ct)) == exit)
            return await exit;

        ct.ThrowIfCancellationRequested();
        _logger.Warning(
            "Claude Code CLI still running {GraceSeconds:F0}s after its result, likely waiting for background commands; stopping it",
            _resultExitGrace.TotalSeconds);
        process.Kill();
        try
        {
            await exit.WaitAsync(StoppedExitTimeout, ct);
        }
        catch (TimeoutException)
        {
            // A detached child can keep stdout open; the result is already in.
        }
        return ExitCodes.Success;
    }

    /// <summary>
    /// Turns the result event's totals into this call's usage. A resumed session reports the whole
    /// conversation, so the totals seen at the end of the previous call are subtracted.
    /// </summary>
    private AgentUsageDetails? BuildUsage(
        ClaudeStreamState state, ClaudeCredential? credential, bool resumed,
        out TokenUsage? usage, out decimal? cost)
    {
        usage = null;
        cost = null;
        var rateLimits = state.RateLimits.Values.ToList();
        var billingMode = credential?.BillingMode ?? AgentBillingModes.Unknown;

        if (state.Totals is not { } totals)
        {
            return rateLimits.Count == 0
                ? null
                : new AgentUsageDetails { BillingMode = billingMode, RateLimits = rateLimits };
        }

        ClaudeUsageTotals? previous = null;
        if (state.SessionId is { } sessionId)
        {
            if (resumed)
                _totalsBySession.TryGetValue(sessionId, out previous);
            _totalsBySession[sessionId] = totals;
        }

        var delta = totals.Minus(previous);
        usage = new TokenUsage
        {
            InputTokens = delta.InputTokens,
            OutputTokens = Math.Max(0, delta.OutputTokens - delta.ThinkingTokens),
            ReasoningTokens = delta.ThinkingTokens,
            CacheReadTokens = delta.CacheReadTokens,
            CacheWriteTokens = delta.CacheWriteTokens
        };
        cost = delta.CostUsd;

        return new AgentUsageDetails
        {
            BillingMode = billingMode,
            Turns = delta.Turns,
            ApiDurationSeconds = delta.ApiDurationMs / 1000d,
            WebSearchRequests = delta.WebSearchRequests,
            ModelUsage = delta.Models.ToDictionary(
                kvp => kvp.Key,
                kvp => new AgentModelUsage
                {
                    InputTokens = kvp.Value.InputTokens,
                    OutputTokens = Math.Max(0, kvp.Value.OutputTokens - kvp.Value.ThinkingTokens),
                    ReasoningTokens = kvp.Value.ThinkingTokens,
                    CacheReadTokens = kvp.Value.CacheReadTokens,
                    CacheWriteTokens = kvp.Value.CacheWriteTokens,
                    WebSearchRequests = kvp.Value.WebSearchRequests,
                    CostUsd = kvp.Value.CostUsd
                }),
            RateLimits = rateLimits
        };
    }

    private void LogInvocation(
        ClaudeStreamState state, int exitCode, TimeSpan elapsed, TokenUsage? usage, decimal? cost,
        AgentUsageDetails? details, AgentErrorCategory errorCategory)
    {
        _logger.Information(
            "Claude Code call finished: exit={ExitCode}, session={SessionId}, model={Model}, elapsed={ElapsedSeconds:F1}s, " +
            "input={InputTokens}, output={OutputTokens}, reasoning={ReasoningTokens}, cache_read={CacheReadTokens}, " +
            "cache_write={CacheWriteTokens}, cost_usd={CostUsd}, billing={BillingMode}, turns={Turns}, " +
            "api_seconds={ApiSeconds:F1}, web_searches={WebSearches}, error_category={ErrorCategory}",
            exitCode, state.SessionId, state.Model ?? _model, elapsed.TotalSeconds,
            usage?.InputTokens, usage?.OutputTokens, usage?.ReasoningTokens, usage?.CacheReadTokens,
            usage?.CacheWriteTokens, cost, details?.BillingMode, details?.Turns,
            details?.ApiDurationSeconds, details?.WebSearchRequests, errorCategory);

        if (details is null)
            return;

        foreach (var (model, modelUsage) in details.ModelUsage)
        {
            _logger.Information(
                "Claude Code model usage: session={SessionId}, model={Model}, input={InputTokens}, output={OutputTokens}, " +
                "reasoning={ReasoningTokens}, cache_read={CacheReadTokens}, cache_write={CacheWriteTokens}, " +
                "web_searches={WebSearches}, cost_usd={CostUsd}",
                state.SessionId, model, modelUsage.InputTokens, modelUsage.OutputTokens, modelUsage.ReasoningTokens,
                modelUsage.CacheReadTokens, modelUsage.CacheWriteTokens, modelUsage.WebSearchRequests, modelUsage.CostUsd);
        }

        foreach (var rateLimit in details.RateLimits)
        {
            _logger.Information(
                "Claude Code rate limit: window={Window}, status={Status}, utilization={Utilization}, resets_at={ResetsAt}",
                rateLimit.Window, rateLimit.Status, rateLimit.Utilization, rateLimit.ResetsAt);
        }
    }

    private AgentResult MissingCredentialResult()
    {
        var message = MissingCredentialMessage();
        _logger.Error("Claude Code auth mode {AuthMode} is selected but its credential is not configured", _authMode);
        return new AgentResult
        {
            ExitCode = ExitCodes.GeneralFailure,
            OutputLines = [message],
            ErrorCategory = AgentErrorCategory.PermanentAuthFailure
        };
    }

    private string MissingCredentialMessage() => _authMode == ClaudeCodeAuthModes.ApiKey
        ? $"Claude Code auth mode '{ClaudeCodeAuthModes.ApiKey}' needs an API key: set claude-api-key in the agent Secret " +
          $"({AgentDefaults.EnvClaudeApiKey}) or {ClaudeCodeCredentials.AnthropicApiKey}."
        : $"Claude Code auth mode '{ClaudeCodeAuthModes.Subscription}' needs a subscription token from `claude setup-token`: " +
          $"set claude-oauth-token in the agent Secret ({AgentDefaults.EnvClaudeOAuthToken}) or {ClaudeCodeCredentials.ClaudeCodeOAuthToken}.";
}
