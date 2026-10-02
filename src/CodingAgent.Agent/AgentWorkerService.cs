using System.Diagnostics;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Polly;

namespace CodingAgent.Agent;

/// <summary>
/// Background service of a chat-mode agent pod (started without <c>--work-item-id</c>). It coordinates
/// the agent lifecycle by composing
/// <see cref="AgentConnectionLifecycle"/> (connection management, heartbeat, reconnection),
/// <see cref="ChatSlotManager"/> (slot acquisition, concurrency control), and
/// <see cref="ChatJobExecutor"/> (chat session and model-fetch handling).
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
///   <item><b>Serve</b> — The hub sends chat prompts, chat cancellations and model fetches,
///     which <see cref="ChatJobExecutor"/> handles.</item>
///   <item><b>Idle</b> — Between requests the agent sends periodic heartbeats until the shutdown signal.</item>
/// </list>
/// <para>
/// Pipeline runs never come through this service: a work-item pod (<see cref="WorkItemAgentService"/>)
/// fetches its assignment over HTTP. Heartbeats are sent every 30 seconds while idle.
/// </para>
/// </remarks>
public sealed class AgentWorkerService : BackgroundService, IAgentService
{
    private readonly AgentConnectionLifecycle _connectionLifecycle;
    private readonly ChatSlotManager _slotManager;
    // S1450 suppressed: this field is used only in the constructor for event wiring, but it
    // must remain a field so tests can access the handler instance via reflection to verify
    // handler behavior in integration with the service's slot manager and lifecycle.
#pragma warning disable S1450
    private readonly ChatJobExecutor _chatJobHandler;
#pragma warning restore S1450
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;

    public AgentWorkerService(AgentWorkerServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.ConnectionLifecycle);
        ArgumentNullException.ThrowIfNull(deps.SlotManager);
        ArgumentNullException.ThrowIfNull(deps.ChatHandler);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _connectionLifecycle = deps.ConnectionLifecycle;
        _slotManager = deps.SlotManager;
        _chatJobHandler = deps.ChatHandler;
        _logger = deps.Logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(deps.Logger);

        var isChatMode = string.Equals(
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true", StringComparison.OrdinalIgnoreCase);
        // T11: these env reads are also available via AgentRuntimeOptions (registered in DI).
        // Consolidating to AgentRuntimeOptions requires injecting it into AgentWorkerServiceDependencies.

        // Wire business event handlers (unconditional)
        _connectionLifecycle.OnAssignChatPrompt += _chatJobHandler.HandleChatPromptAsync;
        _connectionLifecycle.OnCancelChat += _chatJobHandler.HandleCancelChatAsync;
        _connectionLifecycle.OnFetchModels += _chatJobHandler.HandleFetchModelsAsync;

        if (isChatMode)
        {
            var chatSessionId = Environment.GetEnvironmentVariable(AgentDefaults.EnvChatSessionId) ?? "";
            if (string.IsNullOrEmpty(chatSessionId))
                _logger.Warning("AgentWorkerService: AGENT_CHAT_MODE=true but AGENT_CHAT_SESSION_ID is not set — this pod may be misconfigured");
            else
                _logger.Information("AgentWorkerService: running in chat mode (session={ChatSessionId})", chatSessionId);
        }
    }

    /// <summary>Whether the agent is currently executing a job. Always false in chat mode.</summary>
    public bool IsBusy => false;

    /// <summary>The current pipeline step being executed. Always null in chat mode.</summary>
    public PipelineStep? CurrentStep => null;

    /// <summary>Whether the hub connection is active.</summary>
    public bool IsConnected => _connectionLifecycle.IsConnected;

    /// <inheritdoc/>
    public void CancelCurrentJob() { /* no-op: chat pods never hold a pipeline job */ }

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
        // Unexpected exceptions from ConnectAndRunAsync propagate to the BackgroundService
        // infrastructure, which logs them via IHostedService exception handling.
        finally
        {
            await ShutdownAsync();
        }
    }

    private async Task ShutdownAsync()
    {
        _logger.Information("Agent shutting down...");

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
