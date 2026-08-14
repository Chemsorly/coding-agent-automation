using System.Diagnostics;
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
/// <see cref="AgentConnectionLifecycle"/> (connection management, heartbeat, reconnection),
/// <see cref="AgentJobSlotManager"/> (slot acquisition, concurrency control),
/// <see cref="ChatJobHandler"/> (chat session and model-fetch handling), and
/// <see cref="ConsolidationJobHandler"/> (consolidation job handling).
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
    // S1450 suppressed: these fields are used only in the constructor for event wiring, but they
    // must remain as fields so tests can access the handler instances via reflection to verify
    // handler behavior in integration with the service's slot manager and lifecycle.
#pragma warning disable S1450
    private readonly ChatJobHandler _chatJobHandler;
    private readonly ConsolidationJobHandler _consolidationJobHandler;
#pragma warning restore S1450
    private readonly IPipelineExecutor _executor;
    private readonly IJobCompletionReporter _completionReporter;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;

    public AgentWorkerService(AgentWorkerServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.ConnectionLifecycle);
        ArgumentNullException.ThrowIfNull(deps.SlotManager);
        ArgumentNullException.ThrowIfNull(deps.ChatHandler);
        ArgumentNullException.ThrowIfNull(deps.ConsolidationHandler);
        ArgumentNullException.ThrowIfNull(deps.Executor);
        ArgumentNullException.ThrowIfNull(deps.CompletionReporter);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _connectionLifecycle = deps.ConnectionLifecycle;
        _slotManager = deps.SlotManager;
        _chatJobHandler = deps.ChatHandler;
        _consolidationJobHandler = deps.ConsolidationHandler;
        _executor = deps.Executor;
        _completionReporter = deps.CompletionReporter;
        _logger = deps.Logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(deps.Logger);

        var isChatMode = string.Equals(
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true", StringComparison.OrdinalIgnoreCase);

        // Wire business event handlers (unconditional)
        _connectionLifecycle.OnAssignChatPrompt += _chatJobHandler.HandleChatPromptAsync;
        _connectionLifecycle.OnCancelChat += _chatJobHandler.HandleCancelChatAsync;
        _connectionLifecycle.OnCancelJob += HandleCancelJobAsync;
        _connectionLifecycle.OnFetchModels += _chatJobHandler.HandleFetchModelsAsync;
        _connectionLifecycle.OnAssignConsolidationJob += _consolidationJobHandler.HandleAssignConsolidationJobAsync;

        // OnAssignJob only in non-chat mode — chat pods must not receive work-item jobs
        if (!isChatMode)
        {
            _connectionLifecycle.OnAssignJob += HandleAssignJobAsync;
        }

        if (isChatMode)
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
