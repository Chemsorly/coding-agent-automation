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
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;
    private readonly string _agentId;
    private readonly bool _isOpenCodeProvider;

    public AgentWorkerService(
        AgentConnectionLifecycle connectionLifecycle,
        AgentJobSlotManager slotManager,
        AgentIdentity agentIdentity,
        IPipelineExecutor executor,
        IConsolidationExecutor consolidationExecutor,
        IJobCompletionReporter completionReporter,
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connectionLifecycle);
        ArgumentNullException.ThrowIfNull(slotManager);
        ArgumentNullException.ThrowIfNull(agentIdentity);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(consolidationExecutor);
        ArgumentNullException.ThrowIfNull(completionReporter);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionLifecycle = connectionLifecycle;
        _slotManager = slotManager;
        _agentId = agentIdentity.Id;
        _executor = executor;
        _consolidationExecutor = consolidationExecutor;
        _completionReporter = completionReporter;
        _orchestrator = orchestrator;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(logger);
        _isOpenCodeProvider = (Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "")
            .Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase);

        // Wire business event handlers
        _connectionLifecycle.OnAssignJob += HandleAssignJobAsync;
        _connectionLifecycle.OnCancelJob += HandleCancelJobAsync;
        _connectionLifecycle.OnAssignChatPrompt += HandleChatPromptAsync;
        _connectionLifecycle.OnCancelChat += HandleCancelChatAsync;
        _connectionLifecycle.OnFetchModels += HandleFetchModelsAsync;
        _connectionLifecycle.OnAssignConsolidationJob += HandleAssignConsolidationJobAsync;
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
            PipelineTelemetry.AgentJobsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));
            _logger.Warning("Rejecting job {JobId} — agent is busy with {ActiveJobId}",
                message.JobId, busyWith);
            try
            {
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobRejected, message.JobId, "Agent is busy");
            }
            catch (Exception ex)
            {
                receiveActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                receiveActivity?.AddException(ex);
                _logger.Warning(ex, "Failed to notify orchestrator of job rejection {JobId}", message.JobId);
            }
            return;
        }

        _logger.Information("Accepted job {JobId} for issue {IssueIdentifier}",
            message.JobId, message.IssueIdentifier);

        _slotManager.SetActiveJobAssignment(message, message.RunType);

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobAccepted, message.JobId, token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            receiveActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            receiveActivity?.AddException(ex);
            _logger.Error(ex, "Failed to send JobAccepted for {JobId}", message.JobId);
            _slotManager.ForceReleaseJobSlot();
            return;
        }

        var jobCts = _slotManager.JobCts!;
        var activeTask = Task.Run(async () =>
        {
            await using var outputBatcher = new OutputBatcher();
            outputBatcher.OnFlush += async lines =>
            {
                try
                {
                    await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportOutputLines, message.JobId, lines);
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
                    jobCts.Token, cancelledLabel: AgentLabels.Cancelled);
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
                // Report completion via the unified reporter
                if (completion is not null)
                    await _completionReporter.ReportCompletionAsync(message.JobId, completion, CancellationToken.None);

                // Only release slot if buffer is empty — otherwise keep _activeJobId set
                // so reconnection re-registers with ActiveJob state, allowing replay
                // TODO: Potential double slot release if DrainBufferAsync (on reconnection thread)
                // and this code path both call ReleaseJobSlotAndSignalReadyAsync concurrently.
                // Not a crash (null-conditional on CTS, _busyLock guards _activeJobId), but could
                // send duplicate AgentReady signals. Consider adding a guard or idempotent check.
                if (_completionReporter is SignalRCompletionReporter signalRReporter && signalRReporter.HasPendingMessages)
                {
                    _logger.Warning("Job slot held for {JobId} — buffer has pending messages awaiting replay", message.JobId);
                }
                else
                {
                    await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
                }
            }
        });
        _slotManager.SetActiveJobTask(activeTask);
    }

    private Task HandleCancelJobAsync(string jobId)
    {
        // TODO: ActiveJobId is read without lock — a concurrent ReleaseJobSlotAndSignalReadyAsync could
        // clear _activeJobId between this check and the CancelCurrentJob() call. Impact is limited to a
        // spurious no-op cancellation or a missed cancel (orchestrator retries), but consider acquiring
        // _busyLock for the comparison to match the original code's thread-safety contract.
        if (_slotManager.ActiveJobId != jobId)
        {
            _logger.Warning("Received CancelJob for {JobId} but active job is {ActiveJobId}",
                jobId, _slotManager.ActiveJobId);
            return Task.CompletedTask;
        }

        _logger.Information("Cancelling job {JobId}", jobId);
        _slotManager.CancelCurrentJob();
        return Task.CompletedTask;
    }

    private async Task HandleChatPromptAsync(ChatPromptMessage message)
    {
        if (!_slotManager.TryAcquireChatSlot(message.SessionId, out var busyWith))
        {
            _logger.Warning("Rejecting chat prompt for session {SessionId} — agent is busy",
                message.SessionId);
            return;
        }

        _logger.Information("Accepted chat prompt for session {SessionId}", message.SessionId);

        var chatCts = _slotManager.ChatCts!;
        var activeTask = Task.Run(async () =>
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
                        await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportChatResponse, response);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to send chat response lines");
                    }
                };

                try
                {
                    var chatWorkspace = AgentDefaults.ChatWorkspacePath;
                    Directory.CreateDirectory(chatWorkspace);

                    if (!message.UseResume && message.McpServers is { Count: > 0 })
                    {
                        WriteMcpConfig(message.McpConfigPath, message.McpServers);
                        await outputBatcher.AddLineAsync(
                            $"🔌 Wrote MCP config with {message.McpServers.Count} server(s) to {message.McpConfigPath}");
                    }

                    if (_isOpenCodeProvider)
                    {
                        (exitCode, error) = await ExecuteChatViaOpenCodeAsync(message, chatWorkspace, outputBatcher, chatCts.Token);
                    }
                    else
                    {
                        (exitCode, error) = await ExecuteChatViaKiroCliAsync(message, chatWorkspace, outputBatcher, chatCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    exitCode = ExitCodes.Cancelled;
                    error = "Chat cancelled";
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Chat execution failed for session {SessionId}", message.SessionId);
                    exitCode = ExitCodes.GeneralFailure;
                    error = ex.Message;
                }
            }

            try
            {
                var completed = new ChatCompletedMessage
                {
                    SessionId = message.SessionId,
                    ExitCode = exitCode,
                    Error = error
                };
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportChatCompleted, completed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to report chat completion for session {SessionId}", message.SessionId);
            }

            _slotManager.ReleaseChatSlot();

            // Do NOT send AgentReady — the chat session is still active.
            // The agent will be released when CancelChat is received (End Chat / navigate away).
        });
        _slotManager.SetActiveChatTask(activeTask);
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
                Timeout = PipelineConstants.DefaultAgentTimeout
            },
            ct,
            onOutputLine: async line => await outputBatcher.AddLineAsync(line));

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
            await _orchestrator.ExecutePromptAsync(
                AgentDefaults.ChatWarmUpPrompt,
                chatWorkspace,
                useResume: false,
                ct);
        }

        // Execute the actual user prompt (always with --resume after warm-up)
        var exitCode = await _orchestrator.ExecutePromptAsync(
            message.Prompt,
            chatWorkspace,
            useResume: true,
            ct,
            onOutputLine: async line =>
            {
                var clean = KiroCliLib.Core.AnsiStripper.Strip(line);
                await outputBatcher.AddLineAsync(clean);
            });

        return (exitCode, null);
    }

    private async Task HandleCancelChatAsync(string sessionId)
    {
        var (activeSessionId, chatTask, cts) = _slotManager.GetChatSlotSnapshot();

        if (activeSessionId != sessionId)
        {
            _logger.Warning("Received CancelChat for {SessionId} but active session is {ActiveSessionId}",
                sessionId, activeSessionId);
            return;
        }

        _logger.Information("Cancelling chat session {SessionId}", sessionId);
        // ObjectDisposedException catch is still necessary: the snapshot captures a live CTS
        // reference, but ReleaseChatSlot() can run AFTER the snapshot was taken (the chat task
        // completes between our snapshot read and the Cancel() call), disposing the CTS.
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (chatTask is not null)
        {
            var completed = await Task.WhenAny(chatTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != chatTask)
                _logger.Warning("Chat task did not complete within timeout after cancellation for session {SessionId}", sessionId);
        }

        // Signal ready — the chat session is over, agent can accept jobs again
        await SignalAgentReadyAsync();
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
                var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
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
            });
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
            });
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
            PipelineTelemetry.AgentJobsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));
            _logger.Warning("Rejecting consolidation job {JobId} — agent is busy with {ActiveJobId}",
                message.JobId, busyWith);
            try
            {
                await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobRejected, message.JobId, "Agent is busy");
            }
            catch (Exception ex)
            {
                receiveActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                receiveActivity?.AddException(ex);
                _logger.Warning(ex, "Failed to notify orchestrator of consolidation job rejection {JobId}", message.JobId);
            }
            return;
        }

        _logger.Information("Accepted consolidation job {JobId} of type {Type}",
            message.JobId, message.Type);

        var jobCts = _slotManager.JobCts!;
        var activeTask = Task.Run(async () =>
        {
            try
            {
                await _consolidationExecutor.ExecuteAsync(
                    message, _connectionLifecycle.Connection, jobCts.Token);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Consolidation job {JobId} failed with unhandled error", message.JobId);

                // Attempt to report failure back to orchestrator
                try
                {
                    var failResult = new ConsolidationJobResult
                    {
                        JobId = message.JobId,
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                    await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportConsolidationComplete, failResult);
                }
                catch (Exception reportEx)
                {
                    _logger.Error(reportEx, "Failed to report consolidation failure for job {JobId}", message.JobId);
                }
            }
            finally
            {
                await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
            }
        });
        _slotManager.SetActiveJobTask(activeTask);
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
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.AgentReady, _agentId);
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
            await GracefulShutdownHelper.CancelAndWaitAsync(
                _slotManager.JobCts,
                _slotManager.ActiveJobTask,
                TimeSpan.FromSeconds(5),
                _logger,
                "Active job shutdown");
        }

        // Cancel active chat session if running
        if (_slotManager.ActiveChatSessionId is not null)
        {
            _logger.Information("Cancelling active chat session {SessionId} due to shutdown", _slotManager.ActiveChatSessionId);
            await GracefulShutdownHelper.CancelAndWaitAsync(
                _slotManager.ChatCts,
                _slotManager.ActiveChatTask,
                TimeSpan.FromSeconds(2),
                _logger,
                "Active chat shutdown");
        }

        // Deregister and close connection
        await _connectionLifecycle.ShutdownAsync();
    }
}
