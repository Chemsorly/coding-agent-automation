using System.Diagnostics;
using CodingAgent.Agent.ClaudeCode;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Agent;

/// <summary>
/// Executes chat session jobs, model-fetch requests, and related lifecycle concerns.
/// Extracted from <see cref="AgentWorkerService"/> to make chat logic independently testable.
/// </summary>
/// <remarks>
/// Receives job assignments via <see cref="AgentConnectionLifecycle"/> events wired in
/// <see cref="AgentWorkerService"/>. Uses <see cref="ChatSlotManager"/> for single-slot
/// concurrency control.
/// </remarks>
public sealed class ChatJobExecutor : IAsyncDisposable
{
    private readonly AgentConnectionLifecycle _connectionLifecycle;
    private readonly ChatSlotManager _slotManager;
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly Func<Task> _signalAgentReady;
    private readonly AgentProviderType _providerType;
    private readonly Func<ChatPromptMessage, IAgentProvider> _claudeCodeProviderFactory;
    private readonly string? _claudeRulesDirectory;
    private readonly string? _openCodeSteeringDirectory;
    private readonly Func<ChatPromptMessage, IAgentProvider> _openCodeProviderFactory;

    /// <summary>
    /// The current conversation's provider (Claude Code, OpenCode), which remembers the session that
    /// follow-ups resume, and the project secrets the first prompt carried: follow-ups carry none.
    /// </summary>
    private readonly Lock _chatProviderLock = new();
    private IAgentProvider? _chatProvider;
    private IReadOnlyDictionary<string, string>? _chatSecrets;
    private readonly bool _isChatMode;
    private readonly TimeSpan _chatTaskCompletionGracePeriod;
    private readonly Serilog.ILogger _logger;
    private readonly Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> _processStarter;

    public ChatJobExecutor(ChatJobExecutorDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.ConnectionLifecycle);
        ArgumentNullException.ThrowIfNull(deps.SlotManager);
        ArgumentNullException.ThrowIfNull(deps.Orchestrator);
        ArgumentNullException.ThrowIfNull(deps.HttpClientFactory);
        ArgumentNullException.ThrowIfNull(deps.HostApplicationLifetime);
        ArgumentNullException.ThrowIfNull(deps.SignalAgentReady);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _connectionLifecycle = deps.ConnectionLifecycle;
        _slotManager = deps.SlotManager;
        _orchestrator = deps.Orchestrator;
        _httpClientFactory = deps.HttpClientFactory;
        _hostApplicationLifetime = deps.HostApplicationLifetime;
        _signalAgentReady = deps.SignalAgentReady;
        _providerType = deps.ProviderType
            ?? (deps.IsOpenCodeProvider ? AgentProviderType.OpenCode : AgentProviderType.KiroCli);
        _claudeCodeProviderFactory = deps.ClaudeCodeProviderFactory
            ?? (message => new ClaudeCodeAgentProvider(
                deps.Logger,
                deps.ChatModel,
                deps.ClaudeCliPath ?? AgentDefaults.ClaudeCliPath,
                AgentEffortLevelExtensions.ParseEffort(deps.ChatEffort),
                message.AgentAuthMode,
                message.McpConfigPath));
        _openCodeProviderFactory = deps.OpenCodeProviderFactory
            ?? (_ => new OpenCodeAgentProvider(_httpClientFactory, deps.Logger, deps.ChatModel));
        _claudeRulesDirectory = deps.ClaudeRulesDirectory;
        _openCodeSteeringDirectory = deps.OpenCodeSteeringDirectory;
        _isChatMode = deps.IsChatMode;
        _chatTaskCompletionGracePeriod = deps.ChatTaskCompletionGracePeriod;
        _logger = deps.Logger;
        _processStarter = deps.ProcessStarter;
    }

    public async Task HandleChatPromptAsync(ChatPromptMessage message)
    {
        if (!_slotManager.TryAcquireChatSlot(message.SessionId, out _))
        {
            _logger.Warning("Rejecting chat prompt for session {SessionId} — agent is busy",
                message.SessionId);
            return;
        }

        _logger.Information("Accepted chat prompt for session {SessionId}", message.SessionId);

        if (_slotManager.ChatCancellationToken is not { } chatToken)
        {
            _logger.Warning("ChatCancellationToken is null after TryAcquireChatSlot for session {SessionId} — releasing slot", message.SessionId);
            _slotManager.ReleaseChatSlot();
            return;
        }

        var activeTask = Task.Run(async () => await RunChatTaskAsync(message, chatToken), CancellationToken.None);
        _slotManager.SetActiveChatTask(activeTask);
    }

    public async Task RunChatTaskAsync(ChatPromptMessage message, CancellationToken chatToken)
    {
        int exitCode = ExitCodes.GeneralFailure;
        string? error = null;

        // Dispose the batcher before reporting completion — flushes any remaining buffered lines
        // to the orchestrator BEFORE the completion message signals the session is done.
        (exitCode, error) = await ExecuteWithBatchedOutputAsync(message, chatToken);

        try
        {
            await ReportChatCompletedAsync(message.SessionId, exitCode, error);
        }
        finally
        {
            // Always release the chat slot — runs even if ReportChatCompletedAsync throws
            // (e.g. SignalR connection dropped). Without this guarantee the agent would be
            // permanently stuck in Busy state.
            _slotManager.ReleaseChatSlot();
        }

        // Do NOT send AgentReady — the chat session is still active.
        // The agent will be released when CancelChat is received (End Chat / navigate away).
    }

    /// <summary>
    /// Creates an <see cref="OutputBatcher"/>, wires its flush handler to send chat response
    /// lines over SignalR, and delegates to <see cref="ExecuteChatWithOutputAsync"/>.
    /// Extracted from <see cref="RunChatTaskAsync"/> to satisfy S1199 (nested code block).
    /// </summary>
    private async Task<(int exitCode, string? error)> ExecuteWithBatchedOutputAsync(
        ChatPromptMessage message, CancellationToken chatToken)
    {
        await using var outputBatcher = new OutputBatcher();
        outputBatcher.OnFlush += async lines =>
        {
            try
            {
                var response = new ChatResponseMessage
                {
                    SessionId = message.SessionId,
                    Lines = lines.ToList()
                };
                await _connectionLifecycle.WaitForRegistrationAsync(CancellationToken.None);
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportChatResponse, response, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to send chat response lines");
            }
        };

        return await ExecuteChatWithOutputAsync(message, outputBatcher, chatToken);
    }

    public async Task<(int exitCode, string? error)> ExecuteChatWithOutputAsync(
        ChatPromptMessage message, OutputBatcher outputBatcher, CancellationToken chatToken)
    {
        try
        {
            var chatWorkspace = string.IsNullOrEmpty(message.ChatWindowId)
                ? AgentDefaults.ChatWorkspacePath           // backward compat: old SignalR agents
                : Path.Combine(AgentDefaults.ChatWorkspacesRoot, message.ChatWindowId);
            Directory.CreateDirectory(chatWorkspace);

            if (!message.UseResume && message.McpServers is { Count: > 0 })
            {
                McpConfigWriter.WriteConfig(message.McpConfigPath, message.McpServers, _providerType);
                await outputBatcher.AddLineAsync(
                    $"🔌 Wrote MCP config with {message.McpServers.Count} server(s) to {message.McpConfigPath}",
                    chatToken);
            }
            else if (!message.UseResume)
            {
                McpConfigWriter.RemoveStaleConfig(message.McpConfigPath, _providerType);
            }

            // Write project steering before dispatching to the provider.
            // For Kiro CLI, this must precede the warm-up prompt which triggers session init
            // (and .kiro/steering/ loading). Only written on first prompt (UseResume = false).
            if (!message.UseResume && !string.IsNullOrEmpty(message.ProjectSteeringContent))
            {
                ChatSteeringWriter.Write(message.ProjectSteeringContent, chatWorkspace, _providerType, _claudeRulesDirectory, _openCodeSteeringDirectory);
                await outputBatcher.AddLineAsync("📋 Wrote project steering to workspace", chatToken);
            }
            else if (!message.UseResume && _providerType is (AgentProviderType.ClaudeCode or AgentProviderType.OpenCode))
            {
                // Claude Code's and OpenCode's steering lives in the user's home, outside the workspace:
                // clear the last conversation's, so a chat without steering does not load another project's.
                ChatSteeringWriter.Write(string.Empty, chatWorkspace, _providerType, _claudeRulesDirectory, _openCodeSteeringDirectory);
            }

            if (!message.UseResume && message.ProjectSecrets is { Count: > 0 })
            {
                await outputBatcher.AddLineAsync($"🔐 Loaded {message.ProjectSecrets.Count} project secret(s) for process injection", chatToken);
            }

            var secrets = ConversationSecrets(message);
            return _providerType switch
            {
                AgentProviderType.OpenCode => await ExecuteChatViaOpenCodeAsync(message, chatWorkspace, outputBatcher, chatToken),
                AgentProviderType.ClaudeCode => await ExecuteChatViaClaudeCodeAsync(message, secrets, chatWorkspace, outputBatcher, chatToken),
                _ => await ExecuteChatViaKiroCliAsync(message, secrets, chatWorkspace, outputBatcher, chatToken)
            };
        }
        catch (OperationCanceledException)
        {
            return (ExitCodes.Cancelled, "Chat cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Chat execution failed for session {SessionId}", message.SessionId);
            return (ExitCodes.GeneralFailure, ex.Message);
        }
    }

    public async Task ReportChatCompletedAsync(string sessionId, int exitCode, string? error)
    {
        try
        {
            var completed = new ChatCompletedMessage
            {
                SessionId = sessionId,
                ExitCode = exitCode,
                Error = error
            };
            await _connectionLifecycle.WaitForRegistrationAsync(CancellationToken.None);
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportChatCompleted, completed,
                CancellationToken.None); // intentional: completion report must reach orchestrator even when chatToken is cancelled
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to report chat completion for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Runs a chat prompt on OpenCode. Like Claude Code, the provider lives as long as the chat
    /// conversation, because it remembers the session that follow-ups continue.
    /// </summary>
    private async Task<(int exitCode, string? error)> ExecuteChatViaOpenCodeAsync(
        ChatPromptMessage message, string chatWorkspace, OutputBatcher outputBatcher, CancellationToken ct)
    {
        var provider = await GetChatProviderAsync(message, _openCodeProviderFactory);

        var result = await provider.ExecuteAsync(
            new AgentRequest
            {
                Prompt = message.Prompt,
                WorkspacePath = chatWorkspace,
                UseResume = message.UseResume,
                // NOTE [WARNING]: EnvironmentVariables (message.ProjectSecrets) is not forwarded here.
                // Secrets are silently dropped for the OpenCode provider path — the "Loaded N secret(s)"
                // log line is emitted before this branch, implying injection that never happens.
                // This is a behavioral regression vs the old process-wide injection which applied to all
                // providers. Fix by adding EnvironmentVariables = message.ProjectSecrets once the
                // OpenCodeAgentProvider's AgentRequest→ProcessStartInfo chain supports per-process injection.
                Timeout = PipelineConstants.DefaultAgentTimeout
            },
            ct,
            onOutputLine: ForwardTo(outputBatcher, ct));

        var exitCode = result.ExitCode;

        // NOTE: Do NOT re-emit result.OutputLines here. The onOutputLine callback
        // already delivered content to the batcher during ExecuteAsync — either via
        // SSE streaming (message.part.updated) or via the HTTP response fallback
        // (when SSE didn't emit). Re-iterating OutputLines causes duplicate display.

        string? error = exitCode != ExitCodes.Success
            ? string.Join("\n", result.OutputLines.TakeLast(3))
            : null;

        return (exitCode, error);
    }

    /// <summary>
    /// Runs a chat prompt on the Claude Code CLI. The provider instance lives as long as the chat
    /// conversation, because it remembers the session to resume: the first prompt (no
    /// <c>UseResume</c>) starts a new provider, follow-ups reuse it. No warm-up is needed.
    /// Project secrets reach the CLI process per call, as with Kiro CLI.
    /// </summary>
    private async Task<(int exitCode, string? error)> ExecuteChatViaClaudeCodeAsync(
        ChatPromptMessage message, IReadOnlyDictionary<string, string>? secrets, string chatWorkspace,
        OutputBatcher outputBatcher, CancellationToken ct)
    {
        var provider = await GetChatProviderAsync(message, _claudeCodeProviderFactory);

        var result = await provider.ExecuteAsync(
            new AgentRequest
            {
                Prompt = message.Prompt,
                WorkspacePath = chatWorkspace,
                UseResume = message.UseResume,
                EnvironmentVariables = secrets,
                Timeout = PipelineConstants.DefaultAgentTimeout
            },
            ct,
            onOutputLine: ForwardTo(outputBatcher, ct));

        string? error = result.ExitCode != ExitCodes.Success
            ? string.Join("\n", result.OutputLines.TakeLast(3))
            : null;

        return (result.ExitCode, error);
    }

    /// <summary>
    /// The conversation's provider: the first prompt (no <c>UseResume</c>) starts a new one,
    /// follow-ups reuse it.
    /// </summary>
    private async Task<IAgentProvider> GetChatProviderAsync(
        ChatPromptMessage message, Func<ChatPromptMessage, IAgentProvider> createProvider)
    {
        IAgentProvider? previous = null;
        IAgentProvider provider;
        lock (_chatProviderLock)
        {
            if (!message.UseResume || _chatProvider is null)
            {
                previous = _chatProvider;
                _chatProvider = createProvider(message);
            }
            provider = _chatProvider;
        }

        if (previous is not null)
            await previous.DisposeAsync();

        return provider;
    }

    /// <summary>
    /// The project secrets for this prompt. Only the first prompt carries them (to limit wire
    /// exposure), so they are kept for the follow-ups of the same conversation.
    /// </summary>
    private IReadOnlyDictionary<string, string>? ConversationSecrets(ChatPromptMessage message)
    {
        lock (_chatProviderLock)
        {
            if (!message.UseResume || message.ProjectSecrets is { Count: > 0 })
                _chatSecrets = message.ProjectSecrets;
            return _chatSecrets;
        }
    }

    /// <summary>
    /// Forwards a provider's output lines to the batcher. Providers call this on their output
    /// thread, where an exception escaping an async void lambda (AddLineAsync cancelled because
    /// the chat ended) would end the agent process.
    /// </summary>
    private Action<string> ForwardTo(OutputBatcher outputBatcher, CancellationToken ct) =>
        line => _ = AddLineSafelyAsync(outputBatcher, line, ct);

    private async Task AddLineSafelyAsync(OutputBatcher outputBatcher, string line, CancellationToken ct)
    {
        try
        {
            await outputBatcher.AddLineAsync(line, ct);
        }
        catch (OperationCanceledException)
        {
            // The chat ended; the line is no longer wanted.
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to forward a chat output line");
        }
    }

    /// <summary>
    /// Disposes the provider of the current chat conversation, if any, and forgets its secrets.
    /// Called when the chat ends and when the executor is disposed on shutdown.
    /// </summary>
    private async Task ReleaseChatProviderAsync()
    {
        IAgentProvider? provider;
        lock (_chatProviderLock)
        {
            provider = _chatProvider;
            _chatProvider = null;
            _chatSecrets = null;
        }

        if (provider is not null)
            await provider.DisposeAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ReleaseChatProviderAsync();

    private async Task<(int exitCode, string? error)> ExecuteChatViaKiroCliAsync(
        ChatPromptMessage message, IReadOnlyDictionary<string, string>? secrets, string chatWorkspace,
        OutputBatcher outputBatcher, CancellationToken ct)
    {
        // On the first prompt (no --resume), Kiro CLI suppresses response text because
        // tool trust isn't established yet. Send a lightweight warm-up prompt first to
        // establish the session, then send the real prompt with --resume.
        if (!message.UseResume)
        {
            _logger.Information("Sending warm-up prompt to establish chat session");
            // NOTE [WARNING]: Warm-up prompt does not forward environmentVariables (message.ProjectSecrets).
            // If secrets are required during session establishment (e.g. MCP server auth tokens),
            // the warm-up child process will not have them. Safe for now because the warm-up prompt
            // is a throwaway session-initialiser that should not need project secrets, but this
            // asymmetry should be re-evaluated if warm-up behaviour changes.
            await _orchestrator.ExecutePromptAsync(
                AgentDefaults.ChatWarmUpPrompt,
                chatWorkspace,
                useResume: false,
                ct);
        }

        // Execute the actual user prompt (always with --resume after warm-up).
        // Project secrets are passed per-process via environmentVariables — they are not
        // set on the parent process environment.
        var exitCode = await _orchestrator.ExecutePromptAsync(
            message.Prompt,
            chatWorkspace,
            useResume: true,
            ct,
            onOutputLine: async line =>
            {
                var clean = KiroCliLib.Core.AnsiStripper.Strip(line);
                await outputBatcher.AddLineAsync(clean, ct);
            },
            environmentVariables: secrets);

        return (exitCode, null);
    }

    public async Task HandleCancelChatAsync(string sessionId)
    {
        var (activeSessionId, chatTask) = _slotManager.GetChatSlotSnapshot();

        if (activeSessionId != sessionId)
        {
            _logger.Warning("Received CancelChat for {SessionId} but active session is {ActiveSessionId}",
                sessionId, activeSessionId);
            return;
        }

        _logger.Information("Cancelling chat session {SessionId}", sessionId);
        _slotManager.CancelChatIfSession(sessionId);

        if (chatTask is not null)
        {
            // TODO: [WARNING] Task.Delay(TimeSpan.FromSeconds(10), _hostApplicationLifetime.ApplicationStopping)
            // throws OperationCanceledException immediately if ApplicationStopping is already cancelled at
            // the point this executes (e.g. container SIGTERM races with orchestrator CancelChat message).
            // That OCE propagates out of HandleCancelChatAsync uncaught, skipping the SignalChatEnd() /
            // StopApplication() / _signalAgentReady() calls below and leaving the agent in an inconsistent state.
            // Consider wrapping the Task.WhenAny block with a try/catch(OperationCanceledException) that
            // falls through to the lifecycle completion calls.
            var completed = await Task.WhenAny(chatTask, Task.Delay(_chatTaskCompletionGracePeriod, _hostApplicationLifetime.ApplicationStopping));
            if (completed != chatTask)
                _logger.Warning("Chat task did not complete within timeout after cancellation for session {SessionId}", sessionId);
        }

        // The conversation is over: its Claude Code session must not be resumed by a later chat.
        await ReleaseChatProviderAsync();

        if (_isChatMode)
        {
            // Signal chat end source so ConnectAndRunAsync returns
            _connectionLifecycle.SignalChatEnd();
            // DO NOT call _signalAgentReady — chat pod must not return to idle pool
            _hostApplicationLifetime.StopApplication();
        }
        else
        {
            // Signal ready — the chat session is over, agent can accept jobs again
            await _signalAgentReady();
        }
    }

    public async Task HandleFetchModelsAsync(FetchModelsRequest request)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath) ?? AgentDefaults.KiroCliPath,
                Arguments = "chat --list-models --format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ChildProcessEnvironment.StripTelemetry(psi);

            using var process = _processStarter(psi);
            if (process is null)
            {
                await ReportFetchModelsError(request.RequestId, "Failed to start kiro-cli process.");
                return;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None); // intentional: process already exited; timeoutCts.Token may be expired
                await ReportFetchModelsError(request.RequestId, $"kiro-cli exited with code {process.ExitCode}: {stderr}");
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var models = new List<AgentModelInfo>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var m in modelsArray.EnumerateArray())
                {
                    models.Add(new AgentModelInfo
                    {
                        ModelId = m.GetProperty("model_id").GetString() ?? "",
                        Description = m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        RateMultiplier = m.TryGetProperty("rate_multiplier", out var r) ? r.GetDouble() : 1.0
                    });
                }
            }

            await _connectionLifecycle.WaitForRegistrationAsync(CancellationToken.None);
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportFetchModelsResult, new FetchModelsResponse
            {
                RequestId = request.RequestId,
                Models = models
            }, CancellationToken.None); // intentional: process already exited successfully; timeoutCts.Token may be expired
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch models for request {RequestId}", request.RequestId);
            await ReportFetchModelsError(request.RequestId, $"Failed to fetch models: {ex.Message}");
        }
    }

    public async Task ReportFetchModelsError(string requestId, string error)
    {
        try
        {
            await _connectionLifecycle.WaitForRegistrationAsync(CancellationToken.None);
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportFetchModelsResult, new FetchModelsResponse
            {
                RequestId = requestId,
                Models = [],
                Error = error
            }, CancellationToken.None); // intentional: error report must reach orchestrator regardless of cancellation
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to report FetchModels error for request {RequestId}", requestId);
        }
    }
}
