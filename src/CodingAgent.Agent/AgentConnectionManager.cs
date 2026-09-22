using System.Diagnostics;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Retry;

namespace CodingAgent.Agent;

/// <summary>
/// Shared connection lifecycle manager for both long-running and ephemeral agents.
/// Encapsulates SignalR connection management, heartbeat loop, Polly resilience,
/// CancelJob forwarding, reconnection with re-registration, and graceful deregistration.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="AgentWorkerService"/> and <see cref="WorkItemAgentService"/> compose
/// this class instead of duplicating connection management logic.
/// </para>
/// <para>
/// The registration gate, <c>SafeDisposeAsync</c>, and the terminal-close reconnect loop
/// (with <see cref="Interlocked.CompareExchange{T}"/> ownership transfer) are extracted into
/// <see cref="ConnectionReconnectCoordinator"/> and shared with <see cref="AgentConnectionLifecycle"/>.
/// </para>
/// </remarks>
public sealed class AgentConnectionManager : IAgentConnectionManager
{
    private readonly ConnectionReconnectCoordinator _coordinator;
    private readonly AgentId _agentId;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _signalRPipeline;
    private readonly IHostApplicationLifetime? _lifetime;

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
        IHubConnectionManager hubManager,
        IHubConnectionManagerFactory hubManagerFactory,
        AgentId agentId,
        Serilog.ILogger logger,
        IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(hubManagerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        // TODO: Validate that agentId is not default(AgentId) (Value == null). Since AgentId is a value type
        // it cannot be null, but default(AgentId) silently propagates null strings to SignalR hub invocations.
        _agentId = agentId;
        _logger = logger;
        _lifetime = lifetime;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(logger);

        _coordinator = new ConnectionReconnectCoordinator(
            initialHubManager: hubManager,
            agentId: agentId.Value,
            factory: hubManagerFactory,
            logger: logger,
            lifetime: lifetime,
            wireHandlers: WireEventHandlers,
            registerAgent: (mgr, ct) =>
            {
                if (_currentRegistration is null)
                    return Task.CompletedTask;
                return _signalRPipeline.ExecuteAsync(async token =>
                    await mgr.Connection.InvokeAsync(HubMethodNames.RegisterAgent, _currentRegistration, token), ct).AsTask();
            },
            afterSuccessfulReconnect: null);

        WireEventHandlers(hubManager);
    }

    /// <inheritdoc />
    public HubConnection Connection => _coordinator.Connection;

    /// <inheritdoc />
    public bool IsConnected => _coordinator.IsConnected;

    /// <inheritdoc />
    public async Task ConnectAndRegisterAsync(AgentRegistrationMessage registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _currentRegistration = registration;

        var hubManager = _coordinator.CurrentManager
            ?? throw new ObjectDisposedException(nameof(AgentConnectionManager));

        // Connect — retry on transient failures (e.g. 404 when an API pod is
        // mid-rollout and the load-balancer routes to an unready instance).
        // 3 attempts: immediate, +1 s, +2 s (total budget ≤ 5 s).
        var connectPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Linear,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<System.Net.Sockets.SocketException>()
                    .Handle<IOException>(),
                OnRetry = args =>
                {
                    _logger.Warning(
                        "Hub connect attempt {Attempt} failed for agent {AgentId}: {Msg}. Retrying in {Delay}.",
                        args.AttemptNumber + 1, _agentId.Value,
                        args.Outcome.Exception?.Message ?? "unknown",
                        args.RetryDelay);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
        await connectPipeline.ExecuteAsync(async token => await hubManager.StartAsync(token), ct);

        // Register with resilience
        await _signalRPipeline.ExecuteAsync(async token =>
            await hubManager.Connection.InvokeAsync(HubMethodNames.RegisterAgent, registration, token), ct);

        _logger.Information("Agent {AgentId} connected and registered", _agentId.Value);

        // Complete the registration gate so waiters can proceed
        _coordinator.CompleteRegistrationGate();

        // Start heartbeat loop
        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeatTask = RunHeartbeatLoopAsync(_heartbeatCts.Token);
    }

    /// <inheritdoc />
    public async Task InvokeAsync(Func<HubConnection, CancellationToken, Task> action, CancellationToken ct)
    {
        await _coordinator.WaitForRegistrationAsync(ct);
        // TODO [WARNING]: TOCTOU gap between WaitForRegistrationAsync returning and reading CurrentManager.
        // If the coordinator is disposed in this window, ObjectDisposedException is thrown (correct).
        // If a fresh reconnect CAS-swaps in a new manager and resets the gate in this window, the action
        // proceeds on the new manager before its own registration completes. This matches pre-refactor
        // behaviour and is an inherent consequence of the volatile-read pattern.
        // (AgentConnectionManager.cs:149 — DotNetSpecialist review)
        var hubManager = _coordinator.CurrentManager
            ?? throw new ObjectDisposedException(nameof(AgentConnectionManager));
        await _signalRPipeline.ExecuteAsync(async token =>
            await action(hubManager.Connection, token), ct);
    }

    /// <inheritdoc />
    public async Task<T> InvokeAsync<T>(Func<HubConnection, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _coordinator.WaitForRegistrationAsync(ct);
        // TODO [WARNING]: Same TOCTOU gap as the non-generic InvokeAsync overload above. See comment there.
        // (AgentConnectionManager.cs:149 — DotNetSpecialist review)
        var hubManager = _coordinator.CurrentManager
            ?? throw new ObjectDisposedException(nameof(AgentConnectionManager));
        T result = default!;
        await _signalRPipeline.ExecuteAsync(async token =>
        {
            result = await action(hubManager.Connection, token);
        }, ct);
        return result;
    }

    /// <inheritdoc />
    public Task WaitForRegistrationAsync(CancellationToken ct)
        => _coordinator.WaitForRegistrationAsync(ct);

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
        // Cancel any waiters on the registration gate so they are not left hanging at shutdown.
        _coordinator.CancelRegistrationGate(_lifetime?.ApplicationStopping ?? CancellationToken.None);

        // Stop heartbeat (thread-safe: prevents double-dispose race with HandleForceDisconnectAsync)
        await StopHeartbeatAsync();

        // Deregister (best-effort) — read manager before coordinator disposes it
        // TODO [WARNING]: TOCTOU race between reading _coordinator.CurrentManager here and
        // _coordinator.DisposeAsync() below. If HandleTerminalClosedAsync CAS-swaps _hubManager to a
        // new manager between these two calls, the old manager is stopped/deregistered but never
        // disposed (the coordinator will dispose the new one). This race existed pre-refactor and is
        // not a regression, but the refactor made the window explicit. Fix: snapshot the manager via
        // Interlocked.Exchange before stopping/deregistering, or accept the existing race.
        // (AgentConnectionManager.cs:196 — DotNetSpecialist review)
        var currentManager = _coordinator.CurrentManager;
        if (currentManager is not null)
        {
            try
            {
                if (currentManager.IsConnected)
                {
                    await currentManager.Connection.InvokeAsync(
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
                await currentManager.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to stop hub connection (best-effort)");
            }
        }

        // Final atomic disposal via the coordinator (Interlocked.Exchange — exactly-once)
        await _coordinator.DisposeAsync();
    }

    // ── Private: Event Handlers ──────────────────────────────────────────

    private void WireEventHandlers(IHubConnectionManager hubManager)
    {
        hubManager.OnCancelJob += HandleCancelJobAsync;
        hubManager.OnForceDisconnect += HandleForceDisconnectAsync;
        hubManager.OnReconnected += HandleReconnectedAsync;
        hubManager.OnClosed += e => HandleTerminalClosedAsync(e);
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
        // Reset the registration gate BEFORE any awaits so callers that land in the
        // reconnect window are held until re-registration succeeds.
        _coordinator.ResetRegistrationGate();

        PipelineTelemetry.AgentReconnections.Add(1);
        _logger.Information("Agent {AgentId} reconnected (connection: {ConnectionId}), re-registering",
            _agentId.Value, connectionId);

        if (_currentRegistration is null)
        {
            _logger.Warning("No registration message available for re-registration after reconnection");
            // Complete the gate so callers are not permanently blocked
            _coordinator.CompleteRegistrationGate();
            return;
        }

        var hubManager = _coordinator.CurrentManager;
        if (hubManager is null)
        {
            _coordinator.CompleteRegistrationGate();
            return;
        }

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await hubManager.Connection.InvokeAsync(
                    HubMethodNames.RegisterAgent, _currentRegistration, token),
                // Fire-and-forget: reconnect event handler has no ambient token; reconnection must proceed
                CancellationToken.None);
            _logger.Information("Agent {AgentId} re-registered after reconnection", _agentId.Value);
            // Gate complete — unblock waiters
            _coordinator.CompleteRegistrationGate();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to re-register agent {AgentId} after reconnection", _agentId.Value);
            // Complete the gate with failure so callers proceed rather than hanging;
            // the underlying Polly retry will handle the hub call failure.
            _coordinator.CompleteRegistrationGate();
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

    internal Task HandleTerminalClosedAsync(Exception? error, int maxAttempts = 10, Func<int, TimeSpan>? delayOverride = null)
        => _coordinator.HandleTerminalClosedAsync(
            error,
            maxAttempts,
            delayOverride,
            appStoppingToken: _lifetime?.ApplicationStopping ?? CancellationToken.None);

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
                        AgentId = _agentId,
                        Timestamp = DateTimeOffset.UtcNow,
                        CurrentStep = stepValue == NullStep ? null : (PipelineStep)stepValue,
                        MemoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
                    };
                    var manager = _coordinator.CurrentManager;
                    if (manager is null) break;
                    await manager.Connection.InvokeAsync(HubMethodNames.Heartbeat, heartbeat, ct);
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
}
