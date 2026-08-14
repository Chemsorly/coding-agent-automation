using System.Diagnostics;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Handles chat session jobs, model-fetch requests, and related lifecycle concerns.
/// Extracted from <see cref="AgentWorkerService"/> to make chat logic independently testable.
/// </summary>
/// <remarks>
/// Receives job assignments via <see cref="AgentConnectionLifecycle"/> events wired in
/// <see cref="AgentWorkerService"/>. Uses <see cref="AgentJobSlotManager"/> for single-slot
/// concurrency control shared with pipeline and consolidation job handlers.
/// </remarks>
public sealed class ChatJobHandler
{
    private readonly AgentConnectionLifecycle _connectionLifecycle;
    private readonly AgentJobSlotManager _slotManager;
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly Func<Task> _signalAgentReady;
    private readonly bool _isOpenCodeProvider;
    private readonly bool _isChatMode;
    private readonly Serilog.ILogger _logger;

    public ChatJobHandler(ChatJobHandlerDependencies deps)
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
        _isOpenCodeProvider = deps.IsOpenCodeProvider;
        _isChatMode = deps.IsChatMode;
        _logger = deps.Logger;
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
                McpConfigWriter.WriteConfig(message.McpConfigPath, message.McpServers);
                await outputBatcher.AddLineAsync(
                    $"🔌 Wrote MCP config with {message.McpServers.Count} server(s) to {message.McpConfigPath}",
                    chatToken);
            }

            // Write project steering before dispatching to the provider.
            // For Kiro CLI, this must precede the warm-up prompt which triggers session init
            // (and .kiro/steering/ loading). Only written on first prompt (UseResume = false).
            if (!message.UseResume && !string.IsNullOrEmpty(message.ProjectSteeringContent))
            {
                ChatSteeringWriter.Write(message.ProjectSteeringContent, chatWorkspace, _isOpenCodeProvider);
                await outputBatcher.AddLineAsync("📋 Wrote project steering to workspace", chatToken);
            }

            if (!message.UseResume && message.ProjectSecrets is { Count: > 0 })
            {
                await outputBatcher.AddLineAsync($"🔐 Loaded {message.ProjectSecrets.Count} project secret(s) for process injection", chatToken);
            }

            if (_isOpenCodeProvider)
            {
                return await ExecuteChatViaOpenCodeAsync(message, chatWorkspace, outputBatcher, chatToken);
            }
            else
            {
                return await ExecuteChatViaKiroCliAsync(message, chatWorkspace, outputBatcher, chatToken);
            }
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
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportChatCompleted, completed,
                CancellationToken.None); // intentional: completion report must reach orchestrator even when chatToken is cancelled
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to report chat completion for session {SessionId}", sessionId);
        }
    }

    private async Task<(int exitCode, string? error)> ExecuteChatViaOpenCodeAsync(
        ChatPromptMessage message, string chatWorkspace, OutputBatcher outputBatcher, CancellationToken ct)
    {
        await using var provider = new OpenCodeAgentProvider(_httpClientFactory, _logger);
        await provider.EnsureSessionAsync(chatWorkspace, ct);

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
            onOutputLine: async line => await outputBatcher.AddLineAsync(line, ct));

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

    private async Task<(int exitCode, string? error)> ExecuteChatViaKiroCliAsync(
        ChatPromptMessage message, string chatWorkspace, OutputBatcher outputBatcher, CancellationToken ct)
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
            environmentVariables: message.ProjectSecrets);

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
            var completed = await Task.WhenAny(chatTask, Task.Delay(TimeSpan.FromSeconds(10), _hostApplicationLifetime.ApplicationStopping));
            if (completed != chatTask)
                _logger.Warning("Chat task did not complete within timeout after cancellation for session {SessionId}", sessionId);
        }

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

            using var process = System.Diagnostics.Process.Start(psi);
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
