using System.Diagnostics;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Polly;

namespace CodingAgent.Agent;

/// <summary>
/// Manages the SignalR connection lifecycle for long-running (SignalR mode) agents.
/// Encapsulates connect/reconnect/heartbeat logic, terminal-close recovery with
/// <see cref="IHostApplicationLifetime.StopApplication"/> on exhaustion, and extended
/// re-registration retry.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the <see cref="HubConnectionManager"/> and <see cref="HubConnectionManagerFactory"/>,
/// handles the Reconnected/Closed events, and exposes business-level events (AssignChatPrompt, CancelChat, FetchModels)
/// for the coordinator (<see cref="AgentWorkerService"/>) to wire its handlers to.
/// </para>
/// <para>
/// The registration gate, <c>SafeDisposeAsync</c>, and the terminal-close reconnect loop
/// (with <see cref="Interlocked.CompareExchange{T}"/> ownership transfer) are extracted into
/// <see cref="ConnectionReconnectCoordinator"/> and shared with <see cref="AgentConnectionManager"/>.
/// </para>
/// </remarks>
public sealed class AgentConnectionLifecycle : IAsyncDisposable
{
    private readonly ConnectionReconnectCoordinator _coordinator;
    private readonly ChatSlotManager _slotManager;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;

    private readonly string _agentId;
    private readonly IReadOnlyList<string> _baseLabels;

    // ── Chat mode fields ──────────────────────────────────────────────────────
    /// <summary>True when the agent pod runs in chat-only mode (AGENT_CHAT_MODE=true).</summary>
    internal bool _isChatMode;

    /// <summary>Chat session identifier injected via AGENT_CHAT_SESSION_ID env var.</summary>
    internal string _chatSessionId = "";

    /// <summary>Chat model override injected via AGENT_CHAT_MODEL env var.</summary>
    internal string? _chatModel;

    /// <summary>Chat effort override injected via AGENT_CHAT_EFFORT env var.</summary>
    internal string? _chatEffort;

    /// <summary>Resolved when SignalChatEnd() is called; unblocks the ConnectAndRunAsync wait.</summary>
    internal readonly TaskCompletionSource _chatEndSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Injectable seam for KiroCliSettingsWriter.ApplyAsync. Tests override this to
    /// capture/verify calls without writing to the real filesystem.
    /// </summary>
    internal Func<string, string?, CancellationToken, Task> KiroCliSettingsApplyFunc { get; set; }
        = (model, effort, ct) => KiroCliSettingsWriter.ApplyAsync(model, effort, ct);

    internal TimeSpan ExtendedRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Fired when the orchestrator assigns an interactive chat prompt.</summary>
    public event Func<ChatPromptMessage, Task>? OnAssignChatPrompt;

    /// <summary>Fired when the orchestrator requests cancellation of the active chat session.</summary>
    public event Func<string, Task>? OnCancelChat;

    /// <summary>Fired when the orchestrator requests a model list fetch.</summary>
    public event Func<FetchModelsRequest, Task>? OnFetchModels;

    public AgentConnectionLifecycle( // NOSONAR S107 — constructor consolidates all DI-resolved deps for this lifecycle manager
        IHubConnectionManager hubManager,
        IHubConnectionManagerFactory hubManagerFactory,
        ChatSlotManager slotManager,
        AgentId agentId,
        IHostApplicationLifetime hostApplicationLifetime,
        Serilog.ILogger logger,
        AgentRuntimeOptions? runtimeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(hubManagerFactory);
        ArgumentNullException.ThrowIfNull(slotManager);
        ArgumentNullException.ThrowIfNull(hostApplicationLifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _slotManager = slotManager;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(logger);

        // TODO: Validate that agentId.Value is not null/empty. default(AgentId) would propagate null
        // to SignalR hub invocations (RegisterAgent, Heartbeat). See AgentConnectionManager for same pattern.
        _agentId = agentId.Value;

        var labelsEnv = runtimeOptions?.AgentLabels
            ?? Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentLabels)
            ?? string.Empty;
        _baseLabels = labelsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            .AsReadOnly();

        // Store chat mode flags for use in ConnectAndRunAsync
        _isChatMode = runtimeOptions?.IsChatMode
            ?? string.Equals(Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true", StringComparison.OrdinalIgnoreCase);
        _chatSessionId = runtimeOptions?.ChatSessionId
            ?? Environment.GetEnvironmentVariable(AgentDefaults.EnvChatSessionId)
            ?? "";
        _chatModel = runtimeOptions?.ChatModel ?? Environment.GetEnvironmentVariable(AgentDefaults.EnvChatModel);
        _chatEffort = runtimeOptions?.ChatEffort ?? Environment.GetEnvironmentVariable(AgentDefaults.EnvChatEffort);

        // Compose the coordinator. It takes ownership of the initial hub manager.
        // afterSuccessfulReconnect is null — chat pods no longer need a drain step
        // (CriticalMessageBuffer was removed; buffer was always empty in chat mode).
        _coordinator = new ConnectionReconnectCoordinator(
            initialHubManager: hubManager,
            agentId: _agentId,
            factory: hubManagerFactory,
            logger: logger,
            lifetime: hostApplicationLifetime,
            wireHandlers: WireEventHandlers,
            registerAgent: (mgr, ct) => _signalRPipeline.ExecuteAsync(async token =>
                await mgr.Connection.InvokeAsync(HubMethodNames.RegisterAgent, BuildRegistrationMessage(), token), ct).AsTask(),
            afterSuccessfulReconnect: null);
    }

    /// <summary>The underlying hub connection for business handlers to invoke server methods.</summary>
    public HubConnection Connection => _coordinator.Connection;

    /// <summary>Whether the hub connection is currently active.</summary>
    public bool IsConnected => _coordinator.IsConnected;

    /// <summary>
    /// Waits until the agent's registration with the orchestrator is complete.
    /// Returns immediately if already registered.
    /// Blocks callers during the reconnect-race window (from reconnect until
    /// <c>RegisterAgent</c> succeeds) so hub invocations are not rejected.
    /// </summary>
    /// <param name="ct">Cancellation token; times out at <c>SignalRTimeout</c> by default.</param>
    public Task WaitForRegistrationAsync(CancellationToken ct)
        => _coordinator.WaitForRegistrationAsync(ct);

    /// <summary>
    /// Connects to the orchestrator, registers the agent, and runs the heartbeat loop
    /// until the <paramref name="stoppingToken"/> is cancelled.
    /// In chat mode (<see cref="_isChatMode"/>), skips the heartbeat loop and instead
    /// awaits <see cref="_chatEndSource"/> before returning.
    /// </summary>
    public async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        var manager = _coordinator.CurrentManager
            ?? throw new ObjectDisposedException(nameof(AgentConnectionLifecycle));

        // Chat mode: apply model/effort settings to ~/.kiro/settings/cli.json before connecting
        if (_isChatMode)
        {
            var model = _chatModel;
            var effort = _chatEffort;
            if (!string.IsNullOrEmpty(model) && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
                await KiroCliSettingsApplyFunc(model, effort, stoppingToken);
        }

        WireEventHandlers(manager);

        // Connect to orchestrator — retry on transient failures (e.g. 404 during API startup,
        // DNS not yet ready, TCP refused). InfiniteRetryPolicy only covers reconnections after
        // a successful initial connect; initial connect failures need their own retry loop.
        var connectAttempt = 0;
        while (true)
        {
            try
            {
                await manager.StartAsync(stoppingToken);
                break; // connected successfully
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                connectAttempt++;
                // Cap at 10 attempts (~5 minutes total with backoff), then give up
                if (connectAttempt >= 10)
                {
                    _logger.Error(ex,
                        "Agent {AgentId}: hub connect failed after {Attempts} attempts — giving up",
                        _agentId, connectAttempt);
                    throw;
                }

                var delaySecs = Math.Min((int)Math.Pow(2, connectAttempt), 30);
                _logger.Warning(ex,
                    "Agent {AgentId}: hub connect attempt {Attempt} failed ({Error}), retrying in {Delay}s",
                    _agentId, connectAttempt, ex.Message, delaySecs);

                try { await Task.Delay(TimeSpan.FromSeconds(delaySecs), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Register with orchestrator
        var registration = BuildRegistrationMessage();

        await _signalRPipeline.ExecuteAsync(async token =>
            await manager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token), stoppingToken);
        _logger.Information("Agent {AgentId} registered with labels [{Labels}]",
            _agentId, string.Join(", ", _baseLabels));

        // Complete the registration gate so waiters can proceed
        _coordinator.CompleteRegistrationGate();

        // Chat mode: wait for SignalChatEnd() signal instead of heartbeat loop
        if (_isChatMode)
        {
            await _chatEndSource.Task.WaitAsync(stoppingToken);
            return;
        }

        // Normal mode: heartbeat loop
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
        var manager = _coordinator.CurrentManager;
        if (manager is null) return;

        // Deregister from orchestrator
        try
        {
            if (manager.IsConnected)
            {
                await manager.Connection.InvokeAsync(HubMethodNames.DeregisterAgent, _agentId,
                    CancellationToken.None); // intentional: ShutdownAsync runs after ApplicationStopping is already signaled; must complete regardless
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
    /// Atomically nulls and disposes the underlying <see cref="HubConnectionManager"/> via the coordinator.
    /// Guarantees exactly-once disposal even if <see cref="DisposeAsync"/> races with
    /// <see cref="ShutdownAsync"/> or <see cref="HandleTerminalClosedAsync"/>.
    /// </summary>
    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();

    // TODO: Event handlers on old HubConnectionManager instances are never unwired before disposal.
    // While disposal should prevent further event firings, explicitly unsubscribing before
    // SafeDisposeAsync would be more defensive and prevent potential GC reference leaks.
    private void WireEventHandlers(IHubConnectionManager hubManager)
    {
        hubManager.OnAssignChatPrompt += msg => OnAssignChatPrompt?.Invoke(msg) ?? Task.CompletedTask;
        hubManager.OnCancelChat += sessionId => OnCancelChat?.Invoke(sessionId) ?? Task.CompletedTask;
        hubManager.OnFetchModels += request => OnFetchModels?.Invoke(request) ?? Task.CompletedTask;
        hubManager.OnReconnected += HandleReconnectedAsync;
        hubManager.OnClosed += error => HandleTerminalClosedAsync(error);
    }

    // delayOverride is a test seam (null in production) so reconnection tests need not wait the real
    // exponential backoff; mirrors AgentConnectionManager.HandleTerminalClosedAsync.
    internal Task HandleTerminalClosedAsync(Exception? error, int maxAttempts = 10, Func<int, TimeSpan>? delayOverride = null)
        => _coordinator.HandleTerminalClosedAsync(
            error,
            maxAttempts,
            delayOverride,
            appStoppingToken: _hostApplicationLifetime.ApplicationStopping);

    /// <summary>
    /// Re-registers the agent with the orchestrator after a SignalR reconnection.
    /// This is critical when the orchestrator pod rolls over — the new pod has no
    /// prior state and won't recognize heartbeats from unregistered agents.
    /// </summary>
    internal async Task HandleReconnectedAsync(string? connectionId)
    {
        // Reset the registration gate BEFORE any awaits so callers that land in
        // the reconnect window are held until re-registration succeeds.
        _coordinator.ResetRegistrationGate();

        PipelineTelemetry.AgentReconnections.Add(1);
        _logger.Information("Re-registering agent {AgentId} after reconnection (connectionId={ConnectionId})",
            _agentId, connectionId);

        var manager = _coordinator.CurrentManager;
        if (manager is null) return; // Already disposed

        var registration = BuildRegistrationMessage();

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await manager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token),
                // Fire-and-forget: reconnect event handler has no ambient token; re-registration must proceed
                CancellationToken.None);
            _logger.Information("Agent {AgentId} re-registered successfully after reconnection", _agentId);
            // Unblock waiters now that re-registration succeeded
            _coordinator.CompleteRegistrationGate();
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

                    var currentManager = _coordinator.CurrentManager;
                    if (currentManager is null) return; // Disposed during retry

                    await currentManager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, ct);
                    _logger.Information("Agent {AgentId} re-registered on extended attempt {Attempt}", _agentId, i + 1);
                    // Unblock waiters on successful extended retry
                    _coordinator.CompleteRegistrationGate();
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.Information("Extended re-registration cancelled during shutdown for agent {AgentId}", _agentId);
                    // TODO [WARNING]: Consider TrySetCanceled() here instead of TrySetResult() to
                    // accurately signal waiters that registration did not complete due to shutdown.
                    // Using TrySetResult() suggests success to callers, which may then attempt hub
                    // invocations on a shutting-down connection. (Correctness Review)
                    // Release waiters on cancellation so they don't hang at shutdown
                    _coordinator.CompleteRegistrationGate();
                    return;
                }
                catch (Exception retryEx)
                {
                    _logger.Warning(retryEx, "Extended re-registration attempt {Attempt}/3 failed for agent {AgentId}", i + 1, _agentId);
                }
            }

            _logger.Fatal("Agent {AgentId} cannot re-register after all recovery attempts, terminating for container restart", _agentId);
            // TODO [WARNING]: Consider TrySetCanceled() here instead of TrySetResult() so that callers
            // unblocked by the gate know the connection is terminal rather than "registered". (Correctness Review)
            // Release waiters before stopping so they don't hang at shutdown
            _coordinator.CompleteRegistrationGate();
            _hostApplicationLifetime.StopApplication();
        }
    }

    private AgentRegistrationMessage BuildRegistrationMessage()
    {
        var labels = _isChatMode
            ? [.. _baseLabels, "chat=true", $"chat-session-id={_chatSessionId}"]
            : _baseLabels;

        return new AgentRegistrationMessage
        {
            AgentId = _agentId,
            Hostname = Environment.MachineName,
            Labels = labels,
            ActiveJob = null
        };
    }

    /// <summary>Test accessor for BuildRegistrationMessage — allows unit tests to verify label construction.</summary>
    internal AgentRegistrationMessage BuildRegistrationMessageForTest() => BuildRegistrationMessage();

    /// <summary>
    /// Signals the chat session end, unblocking <see cref="ConnectAndRunAsync"/> in chat mode.
    /// Idempotent — safe to call multiple times (uses TrySetResult).
    /// </summary>
    public void SignalChatEnd()
    {
        _chatEndSource.TrySetResult();
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var manager = _coordinator.CurrentManager;
        if (manager is null) return;

        var heartbeat = new HeartbeatMessage
        {
            AgentId = _agentId,
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = null,
            MemoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
        };

        await manager.Connection.InvokeAsync(HubMethodNames.Heartbeat, heartbeat, ct);
    }
}
