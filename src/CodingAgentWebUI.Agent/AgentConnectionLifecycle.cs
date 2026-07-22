using System.Diagnostics;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Polly;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Manages the SignalR connection lifecycle for long-running (SignalR mode) agents.
/// Encapsulates connect/reconnect/heartbeat logic, terminal-close recovery with
/// <see cref="IHostApplicationLifetime.StopApplication"/> on exhaustion, extended
/// re-registration retry, and critical message buffer drain after reconnection.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the <see cref="HubConnectionManager"/> and <see cref="HubConnectionManagerFactory"/>,
/// handles the Reconnected/Closed events, and exposes business-level events (AssignJob, CancelJob, etc.)
/// for the coordinator (<see cref="AgentWorkerService"/>) to wire its handlers to.
/// </para>
/// <para>
/// After successful reconnection, <see cref="DrainBufferAsync"/> replays buffered completion
/// messages and releases the job slot if the buffer empties.
/// </para>
/// </remarks>
public sealed class AgentConnectionLifecycle : IAsyncDisposable
{
    private volatile HubConnectionManager? _hubManager;
    private readonly HubConnectionManagerFactory _hubManagerFactory;
    private readonly SignalRCompletionReporter _completionReporter;
    private readonly AgentJobSlotManager _slotManager;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;

    private readonly string _agentId;
    private readonly IReadOnlyList<string> _labels;

    internal TimeSpan ExtendedRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Fired when the orchestrator assigns a job to this agent.</summary>
    public event Func<JobAssignmentMessage, Task>? OnAssignJob;

    /// <summary>Fired when the orchestrator requests cancellation of the current job.</summary>
    public event Func<string, Task>? OnCancelJob;

    /// <summary>Fired when the orchestrator assigns an interactive chat prompt.</summary>
    public event Func<ChatPromptMessage, Task>? OnAssignChatPrompt;

    /// <summary>Fired when the orchestrator requests cancellation of the active chat session.</summary>
    public event Func<string, Task>? OnCancelChat;

    /// <summary>Fired when the orchestrator requests a model list fetch.</summary>
    public event Func<FetchModelsRequest, Task>? OnFetchModels;

    /// <summary>Fired when the orchestrator assigns a consolidation job.</summary>
    public event Func<ConsolidationJobMessage, Task>? OnAssignConsolidationJob;

    public AgentConnectionLifecycle(
        HubConnectionManager hubManager,
        HubConnectionManagerFactory hubManagerFactory,
        SignalRCompletionReporter completionReporter,
        AgentJobSlotManager slotManager,
        AgentIdentity agentIdentity,
        IHostApplicationLifetime hostApplicationLifetime,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(hubManagerFactory);
        ArgumentNullException.ThrowIfNull(completionReporter);
        ArgumentNullException.ThrowIfNull(slotManager);
        ArgumentNullException.ThrowIfNull(agentIdentity);
        ArgumentNullException.ThrowIfNull(hostApplicationLifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _hubManager = hubManager;
        _hubManagerFactory = hubManagerFactory;
        _completionReporter = completionReporter;
        _slotManager = slotManager;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(logger);

        _agentId = agentIdentity.Id;

        var labelsEnv = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentLabels) ?? string.Empty;
        _labels = labelsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>The underlying hub connection for business handlers to invoke server methods.</summary>
    public HubConnection Connection => _hubManager?.Connection
        ?? throw new ObjectDisposedException(nameof(AgentConnectionLifecycle));

    /// <summary>Whether the hub connection is currently active.</summary>
    public bool IsConnected => _hubManager?.IsConnected ?? false;

    /// <summary>
    /// Connects to the orchestrator, registers the agent, and runs the heartbeat loop
    /// until the <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    public async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        var manager = _hubManager
            ?? throw new ObjectDisposedException(nameof(AgentConnectionLifecycle));

        WireEventHandlers(manager);

        // Connect to orchestrator
        await manager.StartAsync(stoppingToken);

        // Register with orchestrator
        var registration = BuildRegistrationMessage();

        await _signalRPipeline.ExecuteAsync(async token =>
            await manager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token), stoppingToken);
        _logger.Information("Agent {AgentId} registered with labels [{Labels}]",
            _agentId, string.Join(", ", _labels));

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
                PipelineTelemetry.AgentHeartbeatFailures.Add(1);
                _logger.Warning(ex, "Heartbeat failed, will retry on next tick");
            }
        }
    }

    /// <summary>
    /// Gracefully shuts down the connection: deregisters from orchestrator and closes the connection.
    /// </summary>
    public async Task ShutdownAsync()
    {
        var manager = _hubManager;
        if (manager is null) return;

        // Deregister from orchestrator
        try
        {
            if (manager.IsConnected)
            {
                await manager.Connection.InvokeAsync(HubMethodNames.DeregisterAgent, _agentId);
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
            await manager.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to close hub connection during shutdown");
        }
    }

    /// <summary>
    /// Atomically nulls and disposes the underlying <see cref="HubConnectionManager"/>.
    /// Uses <see cref="Interlocked.Exchange{T}"/> to guarantee exactly-once disposal,
    /// even if <see cref="DisposeAsync"/> races with <see cref="ShutdownAsync"/> or
    /// <see cref="HandleTerminalClosedAsync"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var manager = Interlocked.Exchange(ref _hubManager, null);
        if (manager is null) return;
        await SafeDisposeAsync(manager);
    }

    // TODO: Event handlers on old HubConnectionManager instances are never unwired before disposal.
    // While disposal should prevent further event firings, explicitly unsubscribing before
    // SafeDisposeAsync would be more defensive and prevent potential GC reference leaks.
    private void WireEventHandlers(HubConnectionManager hubManager)
    {
        hubManager.OnAssignJob += msg => OnAssignJob?.Invoke(msg) ?? Task.CompletedTask;
        hubManager.OnCancelJob += jobId => OnCancelJob?.Invoke(jobId) ?? Task.CompletedTask;
        hubManager.OnAssignChatPrompt += msg => OnAssignChatPrompt?.Invoke(msg) ?? Task.CompletedTask;
        hubManager.OnCancelChat += sessionId => OnCancelChat?.Invoke(sessionId) ?? Task.CompletedTask;
        hubManager.OnFetchModels += request => OnFetchModels?.Invoke(request) ?? Task.CompletedTask;
        hubManager.OnAssignConsolidationJob += msg => OnAssignConsolidationJob?.Invoke(msg) ?? Task.CompletedTask;
        hubManager.OnReconnected += HandleReconnectedAsync;
        hubManager.OnClosed += HandleTerminalClosedAsync;
    }

    private async Task HandleTerminalClosedAsync(Exception? error)
    {
        _logger.Warning(error, "SignalR connection entered terminal Closed state, attempting fresh reconnection");

        var ct = _hostApplicationLifetime.ApplicationStopping;
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var delay = CalculateReconnectionDelay(attempt);
            _logger.Information("Reconnection attempt {Attempt}/{Max} after {Delay:F1}s",
                attempt, maxAttempts, delay.TotalSeconds);

            HubConnectionManager? newManager = null;
            try
            {
                await Task.Delay(delay, ct);

                var oldManager = _hubManager;
                if (oldManager is null) return; // Already disposed

                newManager = _hubManagerFactory.Create();
                WireEventHandlers(newManager);
                await newManager.StartAsync(ct);

                // Register BEFORE transferring ownership — if registration fails,
                // newManager is still non-null and the catch block disposes it correctly.
                var registration = BuildRegistrationMessage();
                await _signalRPipeline.ExecuteAsync(async token =>
                    await newManager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token), ct);

                // TODO: TOCTOU race — if DisposeAsync runs between reading oldManager and this assignment,
                // newManager will be written into _hubManager after DisposeAsync has already completed,
                // leaking the connection. Use Interlocked.CompareExchange(ref _hubManager, newManager, oldManager)
                // and dispose newManager if the CAS fails.
                _hubManager = newManager;
                newManager = null; // Ownership transferred — skip disposal

                // Dispose old manager after successful swap.
                // Safe: DisposeAsync does not fire the Closed event (no re-entrant call).
                await SafeDisposeAsync(oldManager);

                _logger.Information("Agent {AgentId} reconnected and re-registered after terminal close", _agentId);
                await DrainBufferAsync();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await SafeDisposeAsync(newManager);
                return;
            }
            catch (Exception ex)
            {
                await SafeDisposeAsync(newManager);
                _logger.Warning(ex, "Reconnection attempt {Attempt} failed", attempt);
            }
        }

        _logger.Error("All {MaxAttempts} reconnection attempts exhausted, shutting down agent", maxAttempts);
        _hostApplicationLifetime.StopApplication();
    }

    /// <summary>
    /// Re-registers the agent with the orchestrator after a SignalR reconnection.
    /// This is critical when the orchestrator pod rolls over — the new pod has no
    /// prior state and won't recognize heartbeats from unregistered agents.
    /// </summary>
    private async Task HandleReconnectedAsync(string? connectionId)
    {
        PipelineTelemetry.AgentReconnections.Add(1);
        _logger.Information("Re-registering agent {AgentId} after reconnection (connectionId={ConnectionId})",
            _agentId, connectionId);

        var manager = _hubManager;
        if (manager is null) return; // Already disposed

        var registration = BuildRegistrationMessage();

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await manager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token), CancellationToken.None);
            _logger.Information("Agent {AgentId} re-registered successfully after reconnection", _agentId);
            await DrainBufferAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to re-register agent {AgentId} after initial retry pipeline, starting extended recovery", _agentId);

            var ct = _hostApplicationLifetime.ApplicationStopping;
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    await Task.Delay(ExtendedRetryDelay, ct);

                    var currentManager = _hubManager;
                    if (currentManager is null) return; // Disposed during retry

                    await currentManager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, ct);
                    _logger.Information("Agent {AgentId} re-registered on extended attempt {Attempt}", _agentId, i + 1);
                    await DrainBufferAsync();
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.Information("Extended re-registration cancelled during shutdown for agent {AgentId}", _agentId);
                    return;
                }
                catch (Exception retryEx)
                {
                    _logger.Warning(retryEx, "Extended re-registration attempt {Attempt}/3 failed for agent {AgentId}", i + 1, _agentId);
                }
            }

            _logger.Fatal("Agent {AgentId} cannot re-register after all recovery attempts, terminating for container restart", _agentId);
            _hostApplicationLifetime.StopApplication();
        }
    }

    /// <summary>
    /// Drains the critical message buffer by replaying each buffered message over
    /// the current SignalR connection. Called after successful reconnection/re-registration.
    /// </summary>
    /// <remarks>
    /// If replay fails for a message, it is re-buffered with an incremented drain attempt
    /// counter. Messages exceeding max drain attempts are dropped. After drain,
    /// the job slot is released if the buffer is empty.
    /// </remarks>
    internal async Task DrainBufferAsync()
    {
        if (!_completionReporter.HasPendingMessages)
            return;

        var criticalMessageBuffer = _completionReporter.Buffer;
        const int maxDrainAttempts = 3;
        var messages = criticalMessageBuffer.DrainAll();
        _logger.Information("Draining critical message buffer: {Count} message(s) pending", messages.Count);

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.DrainAttempts >= maxDrainAttempts)
            {
                _logger.Warning(
                    "Dropping buffered message after {MaxAttempts} drain attempts: {MessageType} for job {JobId}",
                    maxDrainAttempts, msg.GetType().Name,
                    (msg as BufferedJobCompleted)?.JobId ?? "unknown");
                continue;
            }

            try
            {
                var manager = _hubManager;
                if (manager is null) return; // Disposed during drain

                switch (msg)
                {
                    case BufferedJobCompleted completed:
                        await _signalRPipeline.ExecuteAsync(async token =>
                            await manager.Connection.InvokeAsync(
                                HubMethodNames.ReportJobCompleted, completed.JobId, completed.Payload, token),
                            CancellationToken.None);
                        _logger.Information("Successfully replayed buffered ReportJobCompleted for job {JobId}", completed.JobId);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "Failed to replay buffered message {MessageType} for job {JobId}, re-buffering (attempt {Attempt}/{Max})",
                    msg.GetType().Name,
                    (msg as BufferedJobCompleted)?.JobId ?? "unknown",
                    msg.DrainAttempts + 1, maxDrainAttempts);

                var rebuffered = msg with { DrainAttempts = msg.DrainAttempts + 1 };
                criticalMessageBuffer.Enqueue(rebuffered);

                // Re-buffer all remaining unprocessed messages to prevent data loss.
                for (var j = i + 1; j < messages.Count; j++)
                {
                    criticalMessageBuffer.Enqueue(messages[j]);
                }

                break; // Stop draining — will retry on next reconnection
            }
        }

        // After drain, release slot if buffer is now empty
        if (!_completionReporter.HasPendingMessages)
            await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
        else
            _logger.Warning("Buffer still has pending messages after drain — job slot remains held");
    }

    private async ValueTask SafeDisposeAsync(HubConnectionManager? manager)
    {
        if (manager is null) return;
        try
        {
            await manager.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Exception during HubConnectionManager disposal (suppressed)");
        }
    }

    private AgentRegistrationMessage BuildRegistrationMessage() => new()
    {
        AgentId = _agentId,
        Hostname = Environment.MachineName,
        Labels = _labels,
        ActiveJob = _slotManager.BuildActiveJobState()
    };

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var manager = _hubManager;
        if (manager is null) return;

        var heartbeat = new HeartbeatMessage
        {
            AgentId = _agentId,
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = _slotManager.CurrentStep,
            MemoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
        };

        await manager.Connection.InvokeAsync(HubMethodNames.Heartbeat, heartbeat, ct);
    }

    internal static TimeSpan CalculateReconnectionDelay(int attempt)
    {
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 120);
        var jitter = Random.Shared.NextDouble(); // 0–1s
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }
}
