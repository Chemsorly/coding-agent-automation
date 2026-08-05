using System.Diagnostics;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;
using Polly;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Shared connection lifecycle manager for both long-running and ephemeral agents.
/// Encapsulates SignalR connection management, heartbeat loop, Polly resilience,
/// CancelJob forwarding, reconnection with re-registration, and graceful deregistration.
/// </summary>
/// <remarks>
/// Both <see cref="AgentWorkerService"/> and <see cref="WorkItemAgentService"/> compose
/// this class instead of duplicating connection management logic.
/// </remarks>
public sealed class AgentConnectionManager : IAgentConnectionManager
{
    private volatile HubConnectionManager _hubManager;
    private readonly HubConnectionManagerFactory _hubManagerFactory;
    private readonly AgentId _agentId;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;

    private volatile AgentRegistrationMessage? _currentRegistration;
    private int _currentStep = NullStep;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    private const int NullStep = -1;

    /// <inheritdoc />
    public event Func<string, Task>? OnCancelJobReceived;

    /// <inheritdoc />
    public event Func<Task>? OnForceDisconnect;

    /// <inheritdoc />
    public event Func<Task>? OnReconnected;

    public AgentConnectionManager(
        HubConnectionManager hubManager,
        HubConnectionManagerFactory hubManagerFactory,
        AgentId agentId,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(hubManagerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _hubManager = hubManager;
        _hubManagerFactory = hubManagerFactory;
        // TODO: Validate that agentId is not default(AgentId) (Value == null). Since AgentId is a value type
        // it cannot be null, but default(AgentId) silently propagates null strings to SignalR hub invocations.
        _agentId = agentId;
        _logger = logger;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(logger);

        WireEventHandlers(_hubManager);
    }

    /// <inheritdoc />
    public HubConnection Connection => _hubManager.Connection;

    /// <inheritdoc />
    public bool IsConnected => _hubManager.IsConnected;

    /// <inheritdoc />
    public async Task ConnectAndRegisterAsync(AgentRegistrationMessage registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _currentRegistration = registration;

        // Connect
        await _hubManager.StartAsync(ct);

        // Register with resilience
        await _signalRPipeline.ExecuteAsync(async token =>
            await _hubManager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token), ct);

        _logger.Information("Agent {AgentId} connected and registered", _agentId.Value);

        // Start heartbeat loop
        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeatTask = RunHeartbeatLoopAsync(_heartbeatCts.Token);
    }

    /// <inheritdoc />
    public async Task InvokeAsync(Func<HubConnection, CancellationToken, Task> action, CancellationToken ct)
    {
        await _signalRPipeline.ExecuteAsync(async token =>
            await action(_hubManager.Connection, token), ct);
    }

    /// <inheritdoc />
    public async Task<T> InvokeAsync<T>(Func<HubConnection, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        T result = default!;
        await _signalRPipeline.ExecuteAsync(async token =>
        {
            result = await action(_hubManager.Connection, token);
        }, ct);
        return result;
    }

    /// <inheritdoc />
    public void UpdateCurrentStep(PipelineStep? step)
    {
        Volatile.Write(ref _currentStep, step.HasValue ? (int)step.Value : NullStep);
    }

    /// <inheritdoc />
    public void UpdateRegistration(AgentRegistrationMessage registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _currentRegistration = registration;
    }

    /// <summary>
    /// Disposes the connection manager: stops heartbeat, deregisters the agent, and closes the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Stop heartbeat (thread-safe: prevents double-dispose race with HandleForceDisconnectAsync)
        await StopHeartbeatAsync();

        // Deregister (best-effort)
        try
        {
            if (_hubManager.IsConnected)
            {
                await _hubManager.Connection.InvokeAsync(
                    HubMethodNames.DeregisterAgent, _agentId.Value,
                    CancellationToken.None); // intentional: best-effort deregistration on dispose; _heartbeatCts is null after StopHeartbeatAsync()
                _logger.Information("Agent {AgentId} deregistered from orchestrator", _agentId.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to deregister agent {AgentId} (best-effort)", _agentId.Value);
        }

        // Stop connection
        try
        {
            await _hubManager.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to stop hub connection (best-effort)");
        }

        await _hubManager.DisposeAsync();
    }

    // ── Private: Event Handlers ──────────────────────────────────────────

    private void WireEventHandlers(HubConnectionManager hubManager)
    {
        hubManager.OnCancelJob += HandleCancelJobAsync;
        hubManager.OnForceDisconnect += HandleForceDisconnectAsync;
        hubManager.OnReconnected += HandleReconnectedAsync;
        hubManager.OnClosed += HandleTerminalClosedAsync;
    }

    private async Task HandleCancelJobAsync(string jobId)
    {
        _logger.Information("Received CancelJob for {JobId}", jobId);
        if (OnCancelJobReceived is not null)
        {
            try
            {
                await OnCancelJobReceived(jobId);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OnCancelJobReceived handler failed for job {JobId}", jobId);
            }
        }
    }

    private async Task HandleForceDisconnectAsync()
    {
        _logger.Warning("Received ForceDisconnect from orchestrator, initiating graceful shutdown");

        // Notify subscribers (WorkItemAgentService will cancel the pipeline)
        if (OnForceDisconnect is not null)
        {
            try
            {
                await OnForceDisconnect();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OnForceDisconnect handler failed");
            }
        }

        // Stop heartbeat (thread-safe: prevents double-dispose race with DisposeAsync)
        await StopHeartbeatAsync();
    }

    private async Task HandleReconnectedAsync(string? connectionId)
    {
        PipelineTelemetry.AgentReconnections.Add(1);
        _logger.Information("Agent {AgentId} reconnected (connection: {ConnectionId}), re-registering",
            _agentId.Value, connectionId);

        if (_currentRegistration is null)
        {
            _logger.Warning("No registration message available for re-registration after reconnection");
            return;
        }

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await _hubManager.Connection.InvokeAsync(
                    HubMethodNames.RegisterAgent, _currentRegistration, token),
                // Fire-and-forget: reconnect event handler has no ambient token; reconnection must proceed
                CancellationToken.None);
            _logger.Information("Agent {AgentId} re-registered after reconnection", _agentId.Value);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to re-register agent {AgentId} after reconnection", _agentId.Value);
        }

        if (OnReconnected is not null)
        {
            try
            {
                await OnReconnected();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OnReconnected handler failed");
            }
        }
    }

    private async Task HandleTerminalClosedAsync(Exception? error)
    {
        _logger.Warning(error, "SignalR connection entered terminal Closed state, attempting fresh reconnection");

        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var delay = ReconnectionHelper.CalculateReconnectionDelay(attempt);
            _logger.Information("Reconnection attempt {Attempt}/{Max} after {Delay:F1}s",
                attempt, maxAttempts, delay.TotalSeconds);

            HubConnectionManager? newManager = null;
            try
            {
                // Fire-and-forget: reconnection delay has no ambient cancellation token in this event handler
                await Task.Delay(delay, CancellationToken.None);

                var oldManager = _hubManager;
                newManager = _hubManagerFactory.Create();
                WireEventHandlers(newManager);
                await newManager.StartAsync(CancellationToken.None);

                // Re-register on new connection
                if (_currentRegistration is not null)
                {
                    await _signalRPipeline.ExecuteAsync(async token =>
                        await newManager.Connection.InvokeAsync(
                            HubMethodNames.RegisterAgent, _currentRegistration, token),
                        // Fire-and-forget: terminal-close reconnect has no ambient token; must complete registration
                        CancellationToken.None);
                }

                _hubManager = newManager;
                newManager = null; // Ownership transferred

                await SafeDisposeAsync(oldManager);

                _logger.Information("Agent {AgentId} reconnected and re-registered after terminal close", _agentId.Value);
                return;
            }
            catch (Exception ex)
            {
                await SafeDisposeAsync(newManager);
                _logger.Warning(ex, "Reconnection attempt {Attempt} failed", attempt);
            }
        }

        _logger.Error("All {MaxAttempts} reconnection attempts exhausted for agent {AgentId}", maxAttempts, _agentId.Value);
    }

    // ── Private: Heartbeat ───────────────────────────────────────────────

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!ct.IsCancellationRequested)
            {
                if (!await heartbeatTimer.WaitForNextTickAsync(ct))
                    break;

                try
                {
                    var stepValue = Volatile.Read(ref _currentStep);
                    var heartbeat = new HeartbeatMessage
                    {
                        AgentId = _agentId.Value,
                        Timestamp = DateTimeOffset.UtcNow,
                        CurrentStep = stepValue == NullStep ? null : (PipelineStep)stepValue,
                        MemoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
                    };
                    await _hubManager.Connection.InvokeAsync(HubMethodNames.Heartbeat, heartbeat, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PipelineTelemetry.AgentHeartbeatFailures.Add(1);
                    _logger.Warning(ex, "Heartbeat failed for agent {AgentId}, will retry on next tick", _agentId.Value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when ct is cancelled
        }
    }

    // ── Private: Utilities ───────────────────────────────────────────────

    /// <summary>
    /// Thread-safe heartbeat cancellation: uses Interlocked.Exchange to atomically
    /// claim the CTS, preventing double-dispose races between ForceDisconnect and DisposeAsync.
    /// Also awaits the heartbeat task to ensure no in-flight sends race with connection disposal.
    /// </summary>
    private async Task StopHeartbeatAsync()
    {
#pragma warning disable 0420 // volatile field passed by reference to Interlocked — safe by design
        var cts = Interlocked.Exchange(ref _heartbeatCts, null);
#pragma warning restore 0420
        if (cts is not null)
        {
            await cts.CancelAsync();
            // Await the heartbeat task to ensure no in-flight hub calls race with connection disposal
            if (_heartbeatTask is not null)
            {
                try { await _heartbeatTask; }
                catch { /* heartbeat loop handles its own exceptions */ }
            }
            cts.Dispose();
        }
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

    }
