using System.Diagnostics;
using System.Text.Json;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Polly;
namespace CodingAgentWebUI.Agent;

/// <summary>
/// Background service that manages the agent lifecycle: connects to the orchestrator,
/// registers, handles job assignments, sends heartbeats, and gracefully shuts down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Event-Driven Lifecycle:</b> This service follows a fully event-driven model driven
/// by SignalR messages from the orchestrator hub. The lifecycle is:
/// </para>
/// <list type="number">
///   <item><b>Connect</b> — <see cref="HubConnectionManager"/> establishes a SignalR connection
///     to the orchestrator with automatic reconnection and exponential backoff.</item>
///   <item><b>Register</b> — The agent sends a registration message (ID, type, labels, capabilities)
///     to the orchestrator, which adds it to the agent registry.</item>
///   <item><b>Receive Job</b> — The orchestrator dispatches a <see cref="Pipeline.Models.JobAssignmentMessage"/>
///     via the <c>AssignJob</c> hub method, triggering <see cref="HubConnectionManager.OnAssignJob"/>.</item>
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
public sealed class AgentWorkerService : BackgroundService
{
    private readonly HubConnectionManager _hubManager;
    private readonly LocalPipelineExecutor _executor;
    private readonly LocalConsolidationExecutor _consolidationExecutor;
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;
    private readonly bool _isOpenCodeProvider;

    private readonly string _agentId;
    private readonly string _agentType;
    private readonly IReadOnlyList<string> _labels;

    private CancellationTokenSource? _jobCts;
    private Task? _activeJobTask;
    private string? _activeJobId;
    private PipelineStep? _currentStep;
    private readonly object _busyLock = new();

    private CancellationTokenSource? _chatCts;
    private Task? _activeChatTask;
    private string? _activeChatSessionId;

    public AgentWorkerService(
        HubConnectionManager hubManager,
        LocalPipelineExecutor executor,
        LocalConsolidationExecutor consolidationExecutor,
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        AgentIdentity agentIdentity,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(consolidationExecutor);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(agentIdentity);
        ArgumentNullException.ThrowIfNull(logger);

        _hubManager = hubManager;
        _executor = executor;
        _consolidationExecutor = consolidationExecutor;
        _orchestrator = orchestrator;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(logger);
        _isOpenCodeProvider = (Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "")
            .Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase);

        _agentId = agentIdentity.Id;
        _agentType = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentType)
            ?? throw new InvalidOperationException("AGENT_TYPE environment variable is required");

        var labelsEnv = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentLabels) ?? string.Empty;
        _labels = labelsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Whether the agent is currently executing a job.</summary>
    public bool IsBusy => _activeJobId is not null;

    /// <summary>The current pipeline step being executed, or null if idle.</summary>
    public PipelineStep? CurrentStep => _currentStep;

    /// <summary>Whether the hub connection is active.</summary>
    public bool IsConnected => _hubManager.IsConnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wire up event handlers
        _hubManager.OnAssignJob += HandleAssignJobAsync;
        _hubManager.OnCancelJob += HandleCancelJobAsync;
        _hubManager.OnAssignChatPrompt += HandleChatPromptAsync;
        _hubManager.OnCancelChat += HandleCancelChatAsync;
        _hubManager.OnFetchModels += HandleFetchModelsAsync;
        _hubManager.OnAssignConsolidationJob += HandleAssignConsolidationJobAsync;
        _hubManager.OnReconnected += HandleReconnectedAsync;

        try
        {
            // Connect to orchestrator
            await _hubManager.StartAsync(stoppingToken);

            // Register with orchestrator
            var registration = BuildRegistrationMessage();

            await _signalRPipeline.ExecuteAsync(async token =>
                await _hubManager.Connection.InvokeAsync("RegisterAgent", registration, token), stoppingToken);
            _logger.Information("Agent {AgentId} registered as {AgentType} with labels [{Labels}]",
                _agentId, _agentType, string.Join(", ", _labels));

            // Heartbeat loop
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await heartbeatTimer.WaitForNextTickAsync(stoppingToken))
                        await SendHeartbeatAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Heartbeat failed, will retry on next tick");
                }
            }
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
        if (!TryAcquireJobSlot(message.JobId, out var busyWith))
        {
            _logger.Warning("Rejecting job {JobId} — agent is busy with {ActiveJobId}",
                message.JobId, busyWith);
            try
            {
                await _hubManager.Connection.InvokeAsync("JobRejected", message.JobId, "Agent is busy");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to notify orchestrator of job rejection {JobId}", message.JobId);
            }
            return;
        }

        _logger.Information("Accepted job {JobId} for issue {IssueIdentifier}",
            message.JobId, message.IssueIdentifier);

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await _hubManager.Connection.InvokeAsync("JobAccepted", message.JobId, token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send JobAccepted for {JobId}", message.JobId);
            lock (_busyLock)
            {
                _activeJobId = null;
            }
            _jobCts?.Dispose();
            _jobCts = null;
            return;
        }

        var jobCts = _jobCts!;
        _activeJobTask = Task.Run(async () =>
        {
            await using var outputBatcher = new OutputBatcher();
            outputBatcher.OnFlush += async lines =>
            {
                try
                {
                    await _hubManager.Connection.InvokeAsync("ReportOutputLines", message.JobId, lines);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to send output lines batch");
                }
            };

            JobCompletionPayload? completion = null;
            try
            {
                completion = await _executor.ExecuteAsync(
                    message, _hubManager.Connection, outputBatcher,
                    step => _currentStep = step,
                    jobCts.Token);
            }
            catch (OperationCanceledException)
            {
                completion = new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsRework = message.LinkedPullRequest is not null
                };
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
                // Report completion
                try
                {
                    if (completion is not null)
                        await _signalRPipeline.ExecuteAsync(async token =>
                            await _hubManager.Connection.InvokeAsync("ReportJobCompleted", message.JobId, completion, token), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to report job completion for {JobId}", message.JobId);
                }

                await ReleaseJobSlotAndSignalReadyAsync();
            }
        });
    }

    private Task HandleCancelJobAsync(string jobId)
    {
        lock (_busyLock)
        {
            if (_activeJobId != jobId)
            {
                _logger.Warning("Received CancelJob for {JobId} but active job is {ActiveJobId}",
                    jobId, _activeJobId);
                return Task.CompletedTask;
            }
        }

        _logger.Information("Cancelling job {JobId}", jobId);
        _jobCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task HandleChatPromptAsync(ChatPromptMessage message)
    {
        if (!TryAcquireChatSlot(message.SessionId, out var busyWith))
        {
            _logger.Warning("Rejecting chat prompt for session {SessionId} — agent is busy",
                message.SessionId);
            return;
        }

        _logger.Information("Accepted chat prompt for session {SessionId}", message.SessionId);

        var chatCts = _chatCts!;
        _activeChatTask = Task.Run(async () =>
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
                        await _hubManager.Connection.InvokeAsync("ReportChatResponse", response);
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
                await _hubManager.Connection.InvokeAsync("ReportChatCompleted", completed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to report chat completion for session {SessionId}", message.SessionId);
            }

            lock (_busyLock)
            {
                _activeChatSessionId = null;
            }

            _chatCts?.Dispose();
            _chatCts = null;

            // Do NOT send AgentReady — the chat session is still active.
            // The agent will be released when CancelChat is received (End Chat / navigate away).
        });
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

        // Send the response text to the UI (HTTP response body contains the final answer)
        foreach (var line in result.OutputLines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                await outputBatcher.AddLineAsync(line);
        }

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
        Task? chatTask;
        lock (_busyLock)
        {
            if (_activeChatSessionId != sessionId)
            {
                _logger.Warning("Received CancelChat for {SessionId} but active session is {ActiveSessionId}",
                    sessionId, _activeChatSessionId);
                return;
            }
            chatTask = _activeChatTask;
        }

        _logger.Information("Cancelling chat session {SessionId}", sessionId);
        var cts = _chatCts;
        cts?.Cancel();

        if (chatTask is not null)
        {
            var completed = await Task.WhenAny(chatTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != chatTask)
                _logger.Warning("Chat task did not complete within timeout after cancellation for session {SessionId}", sessionId);
        }

        // Signal ready — the chat session is over, agent can accept jobs again
        await SignalAgentReadyAsync();
    }

    /// <summary>
    /// Re-registers the agent with the orchestrator after a SignalR reconnection.
    /// This is critical when the orchestrator pod rolls over — the new pod has no
    /// prior state and won't recognize heartbeats from unregistered agents.
    /// </summary>
    private async Task HandleReconnectedAsync(string? connectionId)
    {
        _logger.Information("Re-registering agent {AgentId} after reconnection (connectionId={ConnectionId})",
            _agentId, connectionId);

        var registration = BuildRegistrationMessage();

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await _hubManager.Connection.InvokeAsync("RegisterAgent", registration, token), CancellationToken.None);
            _logger.Information("Agent {AgentId} re-registered successfully after reconnection", _agentId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to re-register agent {AgentId} after reconnection", _agentId);
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

            await _hubManager.Connection.InvokeAsync("ReportFetchModelsResult", new FetchModelsResponse
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
            await _hubManager.Connection.InvokeAsync("ReportFetchModelsResult", new FetchModelsResponse
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
        if (!TryAcquireJobSlot(message.JobId, out var busyWith))
        {
            _logger.Warning("Rejecting consolidation job {JobId} — agent is busy with {ActiveJobId}",
                message.JobId, busyWith);
            // TODO: Send rejection notification to orchestrator (requires verifying orchestrator handles consolidation job rejections)
            return;
        }

        _logger.Information("Accepted consolidation job {JobId} of type {Type}",
            message.JobId, message.Type);

        var jobCts = _jobCts!;
        _activeJobTask = Task.Run(async () =>
        {
            try
            {
                await _consolidationExecutor.ExecuteAsync(
                    message, _hubManager.Connection, jobCts.Token);
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
                    await _hubManager.Connection.InvokeAsync("ReportConsolidationComplete", failResult);
                }
                catch (Exception reportEx)
                {
                    _logger.Error(reportEx, "Failed to report consolidation failure for job {JobId}", message.JobId);
                }
            }
            finally
            {
                await ReleaseJobSlotAndSignalReadyAsync();
            }
        });
    }

    /// <summary>
    /// Writes MCP server configuration to the specified file path.
    /// Delegates to <see cref="McpConfigWriter.WriteConfig"/> for the shared implementation.
    /// </summary>
    private static void WriteMcpConfig(string fullPath, IReadOnlyList<McpServerConfig> mcpServers)
        => McpConfigWriter.WriteConfig(fullPath, mcpServers);

    private bool TryAcquireJobSlot(string jobId, out string? busyWith)
    {
        lock (_busyLock)
        {
            if (_activeJobId is not null || _activeChatSessionId is not null)
            {
                busyWith = _activeJobId ?? $"chat:{_activeChatSessionId}";
                return false;
            }

            _activeJobId = jobId;
            _jobCts = new CancellationTokenSource();
            busyWith = null;
            return true;
        }
    }

    private async Task ReleaseJobSlotAndSignalReadyAsync()
    {
        lock (_busyLock)
        {
            _activeJobId = null;
            _currentStep = null;
        }

        _jobCts?.Dispose();
        _jobCts = null;

        await SignalAgentReadyAsync();
    }

    private bool TryAcquireChatSlot(string sessionId, out string? busyWith)
    {
        lock (_busyLock)
        {
            if (_activeJobId is not null || _activeChatSessionId is not null)
            {
                busyWith = _activeJobId ?? $"chat:{_activeChatSessionId}";
                return false;
            }

            _activeChatSessionId = sessionId;
            _chatCts = new CancellationTokenSource();
            busyWith = null;
            return true;
        }
    }

    private async Task SignalAgentReadyAsync()
    {
        try
        {
            await _hubManager.Connection.InvokeAsync("AgentReady", _agentId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to send AgentReady signal");
        }
    }

    private AgentRegistrationMessage BuildRegistrationMessage() => new()
    {
        AgentId = _agentId,
        Hostname = Environment.MachineName,
        AgentType = _agentType,
        Labels = _labels
    };

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var heartbeat = new HeartbeatMessage
        {
            AgentId = _agentId,
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = _currentStep,
            MemoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
        };

        await _hubManager.Connection.InvokeAsync("Heartbeat", heartbeat, ct);
    }

    private async Task ShutdownAsync()
    {
        _logger.Information("Agent {AgentId} shutting down...", _agentId);

        // Cancel active job if running
        if (_activeJobId is not null)
        {
            _logger.Information("Cancelling active job {JobId} due to shutdown", _activeJobId);
            await GracefulShutdownHelper.CancelAndWaitAsync(
                _jobCts,
                _activeJobTask,
                TimeSpan.FromSeconds(30),
                _logger,
                "Active job shutdown");
        }

        // Deregister from orchestrator
        try
        {
            if (_hubManager.IsConnected)
            {
                await _hubManager.Connection.InvokeAsync("DeregisterAgent", _agentId);
                _logger.Information("Agent {AgentId} deregistered from orchestrator", _agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to deregister agent during shutdown");
        }

        // Close connection
        try
        {
            await _hubManager.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to close hub connection during shutdown");
        }
    }
}
