using System.Diagnostics;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Polly;
namespace CodingAgentWebUI.Agent;

/// <summary>
/// Background service that coordinates the agent lifecycle by composing
/// <see cref="AgentConnectionLifecycle"/> (connection management, heartbeat, reconnection)
/// and <see cref="AgentJobSlotManager"/> (slot acquisition, concurrency control).
/// </summary>
/// <remarks>
/// <para>
/// <b>Event-Driven Lifecycle:</b> This service follows a fully event-driven model driven
/// by SignalR messages from the orchestrator hub. The lifecycle is:
/// </para>
/// <list type="number">
///   <item><b>Connect</b> — <see cref="AgentConnectionLifecycle"/> establishes a SignalR connection
///     to the orchestrator with automatic reconnection and exponential backoff.</item>
///   <item><b>Register</b> — The agent sends a registration message (ID, type, labels, capabilities)
///     to the orchestrator, which adds it to the agent registry.</item>
///   <item><b>Receive Job</b> — The orchestrator dispatches a <see cref="Pipeline.Models.JobAssignmentMessage"/>
///     via the <c>AssignJob</c> hub method, triggering the assign job handler.</item>
///   <item><b>Execute</b> — <see cref="LocalPipelineExecutor"/> runs the full pipeline locally,
///     reporting progress back to the orchestrator via hub invocations.</item>
///   <item><b>Report</b> — On completion (success or failure), the agent sends a
///     <c>JobCompleted</c> message with the result payload.</item>
///   <item><b>Idle</b> — The agent returns to idle state, sending periodic heartbeats until
///     the next job assignment or shutdown signal.</item>
/// </list>
/// <para>
/// Heartbeats are sent every 30 seconds while idle. The orchestrator uses heartbeat absence
/// to detect stale agents via <c>HeartbeatMonitorService</c>.
/// </para>
/// </remarks>
public sealed class AgentWorkerService : BackgroundService, IAgentService
{
    private readonly AgentConnectionLifecycle _connectionLifecycle;
    private readonly AgentJobSlotManager _slotManager;
    private readonly IPipelineExecutor _executor;
    private readonly IConsolidationExecutor _consolidationExecutor;
    private readonly IJobCompletionReporter _completionReporter;
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;
    private readonly string _agentId;
    private readonly bool _isOpenCodeProvider;
    private readonly bool _isChatMode;

    public AgentWorkerService(AgentWorkerServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.ConnectionLifecycle);
        ArgumentNullException.ThrowIfNull(deps.SlotManager);
        ArgumentNullException.ThrowIfNull(deps.Executor);
        ArgumentNullException.ThrowIfNull(deps.ConsolidationExecutor);
        ArgumentNullException.ThrowIfNull(deps.CompletionReporter);
        ArgumentNullException.ThrowIfNull(deps.Orchestrator);
        ArgumentNullException.ThrowIfNull(deps.HttpClientFactory);
        ArgumentNullException.ThrowIfNull(deps.HostApplicationLifetime);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _connectionLifecycle = deps.ConnectionLifecycle;
        _slotManager = deps.SlotManager;
        // TODO: Validate agentId.Value is not null/empty — default(AgentId) would propagate null.
        _agentId = deps.AgentId.Value;
        _executor = deps.Executor;
        _consolidationExecutor = deps.ConsolidationExecutor;
        _completionReporter = deps.CompletionReporter;
        _orchestrator = deps.Orchestrator;
        _httpClientFactory = deps.HttpClientFactory;
        _hostApplicationLifetime = deps.HostApplicationLifetime;
        _logger = deps.Logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(deps.Logger);
        _isOpenCodeProvider = (Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "")
            .Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase);
        _isChatMode = string.Equals(
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true", StringComparison.OrdinalIgnoreCase);

        // Wire business event handlers (unconditional)
        _connectionLifecycle.OnAssignChatPrompt += HandleChatPromptAsync;
        _connectionLifecycle.OnCancelChat += HandleCancelChatAsync;
        _connectionLifecycle.OnCancelJob += HandleCancelJobAsync;
        _connectionLifecycle.OnFetchModels += HandleFetchModelsAsync;
        _connectionLifecycle.OnAssignConsolidationJob += HandleAssignConsolidationJobAsync;

        // OnAssignJob only in non-chat mode — chat pods must not receive work-item jobs
        if (!_isChatMode)
        {
            _connectionLifecycle.OnAssignJob += HandleAssignJobAsync;
        }

        if (_isChatMode)
        {
            var chatSessionId = Environment.GetEnvironmentVariable(AgentDefaults.EnvChatSessionId) ?? "";
            if (string.IsNullOrEmpty(chatSessionId))
                _logger.Warning("AgentWorkerService: AGENT_CHAT_MODE=true but AGENT_CHAT_SESSION_ID is not set — this pod may be misconfigured");
            else
                _logger.Information("AgentWorkerService: running in chat mode (session={ChatSessionId})", chatSessionId);
        }
    }

    /// <summary>Whether the agent is currently executing a job.</summary>
    public bool IsBusy => _slotManager.IsBusy;

    /// <summary>The current pipeline step being executed, or null if idle.</summary>
    public PipelineStep? CurrentStep => _slotManager.CurrentStep;

    /// <summary>Whether the hub connection is active.</summary>
    public bool IsConnected => _connectionLifecycle.IsConnected;

    /// <inheritdoc/>
    public void CancelCurrentJob() => _slotManager.CancelCurrentJob();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _connectionLifecycle.ConnectAndRunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Agent worker service encountered a fatal error");
            throw;
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private async Task HandleAssignJobAsync(JobAssignmentMessage message)
    {
        PipelineTelemetry.AgentJobsReceived.Add(1);

        using var receiveActivity = PipelineTelemetry.ActivitySource.StartActivity("Agent.ReceiveJob");
        receiveActivity?.SetTag("job_id", message.JobId);
        receiveActivity?.SetTag("run_type", "implementation");

        if (!_slotManager.TryAcquireJobSlot(message.JobId, out var busyWith))
        {
            await RejectJobBusyAsync(message.JobId, busyWith, receiveActivity);
            return;
        }

        _logger.Information("Accepted job {JobId} for issue {IssueIdentifier}",
            message.JobId, message.IssueIdentifier);

        _slotManager.SetActiveJobAssignment(message, message.RunType);

        if (!await SendJobAcceptedAsync(message.JobId, receiveActivity))
            return;

        var jobToken = _slotManager.JobCancellationToken!.Value;
        var activeTask = Task.Run(async () => await RunJobTaskAsync(message, jobToken), CancellationToken.None);
        _slotManager.SetActiveJobTask(activeTask);
    }

    private async Task RejectJobBusyAsync(string jobId, string? busyWith, Activity? activity)
    {
        PipelineTelemetry.AgentJobsRejected.Add(1,
            new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));
        _logger.Warning("Rejecting job {JobId} — agent is busy with {ActiveJobId}", jobId, busyWith);
        try
        {
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobRejected, jobId, "Agent is busy", CancellationToken.None);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Failed to notify orchestrator of job rejection {JobId}", jobId);
        }
    }

    private async Task<bool> SendJobAcceptedAsync(string jobId, Activity? activity)
    {
        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobAccepted, jobId, token),
                // Fire-and-forget: job assignment event handler has no ambient token; acceptance must be sent
                CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Error(ex, "Failed to send JobAccepted for {JobId}", jobId);
            _slotManager.ForceReleaseJobSlot();
            return false;
        }
    }

    private async Task RunJobTaskAsync(JobAssignmentMessage message, CancellationToken jobToken)
    {
        await using var outputBatcher = new OutputBatcher();
        outputBatcher.OnFlush += async lines =>
        {
            try
            {
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportOutputLines, message.JobId, lines,
                    CancellationToken.None); // intentional: fire-and-forget flush callback; no ambient token available
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to send output lines batch");
            }
        };

        JobCompletionPayload? completion = null;
        try
        {
            completion = await AgentJobRunner.ExecuteAsync(
                _executor, message, _connectionLifecycle.Connection, outputBatcher,
                step => _slotManager.SetCurrentStep(step),
                cancelledLabel: AgentLabels.Cancelled, ct: jobToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline execution failed for job {JobId}", message.JobId);
            completion = new JobCompletionPayload
            {
                FinalStep = PipelineStep.Failed,
                FailureReason = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
                IsRework = message.LinkedPullRequest is not null
            };
        }
        finally
        {
            await FinalizeJobAsync(message.JobId, completion);
        }
    }

    private async Task FinalizeJobAsync(string jobId, JobCompletionPayload? completion)
    {
        // Report completion via the unified reporter
        if (completion is not null)
            // Fire-and-forget: called in finally block where job token may already be cancelled; completion must always be reported
            await _completionReporter.ReportCompletionAsync(jobId, completion, CancellationToken.None);

        // Only release slot if buffer is empty — otherwise keep _activeJobId set
        // so reconnection re-registers with ActiveJob state, allowing replay
        if (_completionReporter is SignalRCompletionReporter signalRReporter && signalRReporter.HasPendingMessages)
        {
            _logger.Warning("Job slot held for {JobId} — buffer has pending messages awaiting replay", jobId);
        }
        else
        {
            await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
        }
    }

    private Task HandleCancelJobAsync(string jobId)
    {
        if (!_slotManager.CancelJobIfMatch(jobId))
        {
            _logger.Warning("Received CancelJob for {JobId} but active job is {ActiveJobId}",
                jobId, _slotManager.ActiveJobId);
            return Task.CompletedTask;
        }

        _logger.Information("Cancelling job {JobId}", jobId);
        return Task.CompletedTask;
    }

    private async Task HandleChatPromptAsync(ChatPromptMessage message)
    {
        if (!_slotManager.TryAcquireChatSlot(message.SessionId, out _))
        {
            _logger.Warning("Rejecting chat prompt for session {SessionId} — agent is busy",
                message.SessionId);
            return;
        }

        _logger.Information("Accepted chat prompt for session {SessionId}", message.SessionId);

        var chatToken = _slotManager.ChatCancellationToken!.Value;
        var activeTask = Task.Run(async () => await RunChatTaskAsync(message, chatToken), CancellationToken.None);
        _slotManager.SetActiveChatTask(activeTask);
    }

    private async Task RunChatTaskAsync(ChatPromptMessage message, CancellationToken chatToken)
    {
        int exitCode = ExitCodes.GeneralFailure;
        string? error = null;

        // Scoped so the batcher is disposed (flushing remaining lines)
        // BEFORE reporting completion to the orchestrator.
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

            (exitCode, error) = await ExecuteChatWithOutputAsync(message, outputBatcher, chatToken);
        }

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

    private async Task<(int exitCode, string? error)> ExecuteChatWithOutputAsync(
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
                WriteMcpConfig(message.McpConfigPath, message.McpServers);
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

            // Secret injection:
            // - KiroCli: secrets are passed as per-process launch env via additionalEnv in ExecuteChatViaKiroCliAsync.
            //   No process-wide mutation — no cleanup needed.
            // - OpenCode: HTTP-based server; secrets are passed via AdditionalEnv on AgentRequest and
            //   injected/cleaned up inside OpenCodeAgentProvider.ExecuteAsync, scoped to that call.
            //   No process-wide mutation in AgentWorkerService.

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

    private async Task ReportChatCompletedAsync(string sessionId, int exitCode, string? error)
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

        // Secrets are passed via AdditionalEnv on the request.
        // OpenCodeAgentProvider.ExecuteAsync injects them into the process-wide environment
        // for the duration of the HTTP call only, then cleans them up in a finally block.
        // This scopes process-wide mutation to the provider's execution span rather than
        // the broader AgentWorkerService lifetime.
        var additionalEnv = !message.UseResume && message.ProjectSecrets is { Count: > 0 }
            ? message.ProjectSecrets
            : null;

        if (additionalEnv is not null)
            await outputBatcher.AddLineAsync($"🔐 Injected {additionalEnv.Count} project secret(s)", ct);

        var result = await provider.ExecuteAsync(
            new AgentRequest
            {
                Prompt = message.Prompt,
                WorkspacePath = chatWorkspace,
                UseResume = message.UseResume,
                Timeout = PipelineConstants.DefaultAgentTimeout,
                AdditionalEnv = additionalEnv
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
            // TODO: The warm-up call does not receive additionalEnv — secrets are unavailable to
            // the child process that runs the warm-up prompt. If the warm-up prompt triggers any
            // tooling that reads the injected secrets (e.g. workspace initialisation that accesses
            // authenticated resources), the first child process will miss them. additionalEnv is
            // computed after this call so it cannot be passed here without restructuring the block.
            await _orchestrator.ExecutePromptAsync(
                AgentDefaults.ChatWarmUpPrompt,
                chatWorkspace,
                useResume: false,
                ct);
        }

        // Secrets are passed via additionalEnv: each entry is injected into
        // ProcessStartInfo.Environment before the child process starts, scoping
        // secrets to the kiro-cli process lifetime without mutating the parent environment.
        // TODO: additionalEnv is only non-null on the first prompt (!message.UseResume). On
        // subsequent resume prompts each call spawns a fresh short-lived kiro-cli process but
        // receives no secrets, so resumed calls cannot access project secrets. Evaluate whether
        // secrets should also be passed on resume calls.
        var additionalEnv = !message.UseResume && message.ProjectSecrets is { Count: > 0 }
            ? message.ProjectSecrets
            : null;

        if (additionalEnv is not null)
            await outputBatcher.AddLineAsync($"🔐 Injected {additionalEnv.Count} project secret(s)", ct);

        // Execute the actual user prompt (always with --resume after warm-up)
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
            additionalEnv: additionalEnv);

        return (exitCode, null);
    }

    private async Task HandleCancelChatAsync(string sessionId)
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
            // DO NOT call SignalAgentReadyAsync — chat pod must not return to idle pool
            _hostApplicationLifetime.StopApplication();
        }
        else
        {
            // Signal ready — the chat session is over, agent can accept jobs again
            await SignalAgentReadyAsync();
        }
    }

    private async Task HandleFetchModelsAsync(FetchModelsRequest request)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath) ?? AgentDefaults.KiroCliPath,
                Arguments = "chat --list-models --format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
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

    private async Task ReportFetchModelsError(string requestId, string error)
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

    private async Task HandleAssignConsolidationJobAsync(ConsolidationJobMessage message)
    {
        PipelineTelemetry.AgentJobsReceived.Add(1);

        using var receiveActivity = PipelineTelemetry.ActivitySource.StartActivity("Agent.ReceiveJob");
        receiveActivity?.SetTag("job_id", message.JobId);
        receiveActivity?.SetTag("run_type", "consolidation");

        if (!_slotManager.TryAcquireJobSlot(message.JobId, out var busyWith))
        {
            await RejectConsolidationJobBusyAsync(message.JobId, busyWith, receiveActivity);
            return;
        }

        _logger.Information("Accepted consolidation job {JobId} of type {Type}",
            message.JobId, message.Type);

        var jobToken = _slotManager.JobCancellationToken!.Value;
        var activeTask = Task.Run(async () => await RunConsolidationTaskAsync(message, jobToken), CancellationToken.None);
        _slotManager.SetActiveJobTask(activeTask);
    }

    private async Task RejectConsolidationJobBusyAsync(string jobId, string? busyWith, Activity? activity)
    {
        PipelineTelemetry.AgentJobsRejected.Add(1,
            new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));
        _logger.Warning("Rejecting consolidation job {JobId} — agent is busy with {ActiveJobId}",
            jobId, busyWith);
        try
        {
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobRejected, jobId, "Agent is busy", CancellationToken.None);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Failed to notify orchestrator of consolidation job rejection {JobId}", jobId);
        }
    }

    private async Task RunConsolidationTaskAsync(ConsolidationJobMessage message, CancellationToken jobToken)
    {
        try
        {
            await _consolidationExecutor.ExecuteAsync(
                message, _connectionLifecycle.Connection, jobToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Consolidation job {JobId} failed with unhandled error", message.JobId);
            await ReportConsolidationFailureAsync(message.JobId, ex.Message);
        }
        finally
        {
            await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
        }
    }

    private async Task ReportConsolidationFailureAsync(string jobId, string errorMessage)
    {
        try
        {
            var failResult = new ConsolidationJobResult
            {
                JobId = jobId,
                Success = false,
                ErrorMessage = errorMessage
            };
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportConsolidationComplete, failResult,
                CancellationToken.None); // intentional: failure report must reach orchestrator even when jobToken is cancelled
        }
        catch (Exception reportEx)
        {
            _logger.Error(reportEx, "Failed to report consolidation failure for job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Writes MCP server configuration to the specified file path.
    /// Delegates to <see cref="McpConfigWriter.WriteConfig"/> for the shared implementation.
    /// </summary>
    private static void WriteMcpConfig(string fullPath, IReadOnlyList<McpServerConfig> mcpServers)
        => McpConfigWriter.WriteConfig(fullPath, mcpServers);

    private async Task SignalAgentReadyAsync()
    {
        try
        {
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.AgentReady, _agentId,
                _hostApplicationLifetime.ApplicationStopping);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to send AgentReady signal");
        }
    }

    private async Task ShutdownAsync()
    {
        _logger.Information("Agent shutting down...");

        // Cancel active job if running
        if (_slotManager.ActiveJobId is not null)
        {
            _logger.Information("Cancelling active job {JobId} due to shutdown", _slotManager.ActiveJobId);
            _slotManager.CancelCurrentJob();
            await GracefulShutdownHelper.CancelAndWaitAsync(
                null,
                _slotManager.ActiveJobTask,
                TimeSpan.FromSeconds(5),
                _logger,
                "Active job shutdown");
        }

        // Cancel active chat session if running
        if (_slotManager.ActiveChatSessionId is not null)
        {
            _logger.Information("Cancelling active chat session {SessionId} due to shutdown", _slotManager.ActiveChatSessionId);
            _slotManager.CancelCurrentChat();
            await GracefulShutdownHelper.CancelAndWaitAsync(
                null,
                _slotManager.ActiveChatTask,
                TimeSpan.FromSeconds(2),
                _logger,
                "Active chat shutdown");
        }

        // Deregister and close connection
        await _connectionLifecycle.ShutdownAsync();
    }
}
