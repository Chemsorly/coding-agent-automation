using CodingAgent.Pipeline.Interfaces;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Agent;

/// <summary>
/// Encapsulates the three connection-management mechanisms that were previously duplicated
/// between <see cref="AgentConnectionLifecycle"/> and <see cref="AgentConnectionManager"/>:
/// <list type="bullet">
///   <item>Registration gate (<see cref="WaitForRegistrationAsync"/>, <see cref="ResetRegistrationGate"/>,
///   <see cref="CompleteRegistrationGate"/>, <see cref="CancelRegistrationGate"/>)</item>
///   <item><see cref="SafeDisposeAsync"/> — suppressed disposal with logging</item>
///   <item><see cref="HandleTerminalClosedAsync"/> — reconnect loop with
///   <see cref="Interlocked.CompareExchange{T}"/> ownership transfer</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This class is <see langword="internal"/> and not DI-registered. Each outer class
/// (<see cref="AgentConnectionLifecycle"/>, <see cref="AgentConnectionManager"/>) constructs
/// one instance in its own constructor.
/// </para>
/// <para>
/// The volatile <c>_hubManager</c> field lives here so the <c>Interlocked</c> operations
/// (CompareExchange in the reconnect loop, Exchange in DisposeAsync) have a stable ref target.
/// Outer classes access the current manager only through <see cref="CurrentManager"/>,
/// <see cref="IsConnected"/>, and <see cref="Connection"/>.
/// </para>
/// </remarks>
internal sealed class ConnectionReconnectCoordinator : IAsyncDisposable
{
    private volatile IHubConnectionManager? _hubManager;
    private readonly string _agentId;
    private readonly IHubConnectionManagerFactory _factory;
    private readonly Serilog.ILogger _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly Action<IHubConnectionManager> _wireHandlers;
    private readonly Func<IHubConnectionManager, CancellationToken, Task> _registerAgent;
    private readonly Func<Task>? _afterSuccessfulReconnect;

    // Registration gate:
    // - Starts as completed (no blocking on first use before any reconnect occurs).
    // - Reset to a new incomplete TCS at the start of every terminal-close handler BEFORE any awaits.
    // - Completed after RegisterAgent succeeds.
    // - Cancelled in DisposeAsync so waiters are not left hanging at shutdown.
    // Declared volatile so the assignment is immediately visible on all threads.
    private volatile TaskCompletionSource _registrationGate = CreateCompletedGate();

    /// <summary>
    /// Initialises the coordinator.
    /// </summary>
    /// <param name="initialHubManager">
    ///   The hub manager created before the coordinator (owned by the outer class constructor).
    ///   Ownership transfers here; the coordinator disposes it via <see cref="DisposeAsync"/>.
    /// </param>
    /// <param name="agentId">Agent identifier for logging.</param>
    /// <param name="factory">Factory used to create replacement managers in the reconnect loop.</param>
    /// <param name="logger">Serilog logger.</param>
    /// <param name="lifetime">
    ///   Optional host lifetime for <see cref="IHostApplicationLifetime.StopApplication"/> on exhaustion
    ///   and <see cref="IHostApplicationLifetime.ApplicationStopping"/> for gate cancellation.
    ///   May be <see langword="null"/> in test contexts that run without a host.
    /// </param>
    /// <param name="wireHandlers">
    ///   Delegate called on every new <see cref="IHubConnectionManager"/> created during reconnection.
    ///   Each outer class supplies its own <c>WireEventHandlers</c> method since the event sets differ.
    /// </param>
    /// <param name="registerAgent">
    ///   Delegate that performs the <c>RegisterAgent</c> hub invocation on a given manager.
    ///   Accepts a <see cref="CancellationToken"/> and may apply Polly resilience internally.
    /// </param>
    /// <param name="afterSuccessfulReconnect">
    ///   Optional callback invoked after a successful reconnect and re-registration.
    ///   <see cref="AgentConnectionLifecycle"/> passes <c>DrainBufferAsync</c>;
    ///   <see cref="AgentConnectionManager"/> passes <see langword="null"/>.
    /// </param>
    public ConnectionReconnectCoordinator(
        IHubConnectionManager initialHubManager,
        string agentId,
        IHubConnectionManagerFactory factory,
        Serilog.ILogger logger,
        IHostApplicationLifetime? lifetime,
        Action<IHubConnectionManager> wireHandlers,
        Func<IHubConnectionManager, CancellationToken, Task> registerAgent,
        Func<Task>? afterSuccessfulReconnect = null)
    {
        ArgumentNullException.ThrowIfNull(initialHubManager);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(wireHandlers);
        ArgumentNullException.ThrowIfNull(registerAgent);

        _hubManager = initialHubManager;
        _agentId = agentId;
        _factory = factory;
        _logger = logger;
        _lifetime = lifetime;
        _wireHandlers = wireHandlers;
        _registerAgent = registerAgent;
        _afterSuccessfulReconnect = afterSuccessfulReconnect;
    }

    // ── Hub manager access ────────────────────────────────────────────────

    /// <summary>
    /// The current hub manager, or <see langword="null"/> after disposal.
    /// Volatile read — always reflects the latest value written by the reconnect loop or <see cref="DisposeAsync"/>.
    /// </summary>
    public IHubConnectionManager? CurrentManager => _hubManager;

    /// <summary>Whether the current hub connection is active.</summary>
    public bool IsConnected => _hubManager?.IsConnected ?? false;

    /// <summary>
    /// The underlying <see cref="HubConnection"/> for direct hub invocations.
    /// Throws <see cref="ObjectDisposedException"/> if the coordinator has been disposed.
    /// </summary>
    public HubConnection Connection => _hubManager?.Connection
        ?? throw new ObjectDisposedException(nameof(ConnectionReconnectCoordinator));

    // ── Registration gate ────────────────────────────────────────────────

    /// <summary>
    /// Waits until the agent's registration with the orchestrator is complete.
    /// Returns immediately if already registered.
    /// Blocks callers during the reconnect-race window (from reconnect until
    /// <c>RegisterAgent</c> succeeds) so hub invocations are not rejected.
    /// </summary>
    public async Task WaitForRegistrationAsync(CancellationToken ct)
    {
        var gate = _registrationGate;
        if (gate.Task.IsCompleted)
            return;

        await ReconnectionHelper.WaitWithTimeoutAsync(gate.Task, ct, _agentId, _logger);
    }

    /// <summary>
    /// Resets the registration gate to a new incomplete <see cref="TaskCompletionSource"/>.
    /// Must be called BEFORE any <see langword="await"/> in the reconnect handler so callers
    /// that arrive during the reconnect window are held.
    /// </summary>
    public void ResetRegistrationGate()
    {
        // TODO [WARNING]: Callers that captured the old gate reference before this assignment
        // (e.g. in WaitForRegistrationAsync) will now wait forever (or until their own timeout)
        // because the old TCS object will never be completed — only the new one will receive
        // TrySetResult. Fix: cancel the old TCS before replacing it, e.g.:
        //   var old = _registrationGate;
        //   _registrationGate = new TaskCompletionSource(...);
        //   old.TrySetCanceled();
        // (ConnectionReconnectCoordinator.cs:168 — DotNetSpecialist review)
        _registrationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes the registration gate, unblocking all current waiters.</summary>
    public void CompleteRegistrationGate()
    {
        _registrationGate.TrySetResult();
    }

    /// <summary>Cancels the registration gate so waiters receive <see cref="OperationCanceledException"/>.</summary>
    public void CancelRegistrationGate(CancellationToken appStopping)
    {
        _registrationGate.TrySetCanceled(appStopping);
    }

    // ── SafeDisposeAsync ─────────────────────────────────────────────────

    /// <summary>
    /// Disposes <paramref name="manager"/>, suppressing and logging any exception.
    /// No-ops when <paramref name="manager"/> is <see langword="null"/>.
    /// </summary>
    public async ValueTask SafeDisposeAsync(IHubConnectionManager? manager)
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

    // ── Terminal-close reconnect loop ────────────────────────────────────

    /// <summary>
    /// Attempts to reconnect and re-register the agent after a terminal SignalR close.
    /// Uses <see cref="Interlocked.CompareExchange{T}"/> to atomically transfer ownership
    /// of the new manager. Calls <see cref="IHostApplicationLifetime.StopApplication"/> on exhaustion.
    /// </summary>
    /// <param name="error">The exception that triggered the close, if any.</param>
    /// <param name="maxAttempts">Maximum reconnection attempts before giving up.</param>
    /// <param name="delayOverride">
    ///   Test seam: overrides <see cref="ReconnectionHelper.CalculateReconnectionDelay"/> per attempt.
    ///   Pass <see langword="null"/> in production.
    /// </param>
    /// <param name="appStoppingToken">
    ///   Used for <c>Task.Delay</c> and cancellation detection.
    ///   Outer class passes <c>_hostApplicationLifetime.ApplicationStopping</c> (lifecycle) or
    ///   <c>_lifetime?.ApplicationStopping ?? CancellationToken.None</c> (manager).
    /// </param>
    public async Task HandleTerminalClosedAsync(
        Exception? error,
        int maxAttempts,
        Func<int, TimeSpan>? delayOverride,
        CancellationToken appStoppingToken)
    {
        // Reset the registration gate BEFORE any awaits so callers that land during
        // the terminal-close recovery window are held until re-registration succeeds.
        ResetRegistrationGate();

        _logger.Warning(error, "SignalR connection entered terminal Closed state, attempting fresh reconnection");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var delay = delayOverride?.Invoke(attempt) ?? ReconnectionHelper.CalculateReconnectionDelay(attempt);
            _logger.Information("Reconnection attempt {Attempt}/{Max} after {Delay:F1}s",
                attempt, maxAttempts, delay.TotalSeconds);

            IHubConnectionManager? newManager = null;
            try
            {
                await Task.Delay(delay, appStoppingToken);

                var oldManager = _hubManager;
                if (oldManager is null) return; // Already disposed

                newManager = _factory.Create();
                _wireHandlers(newManager);
                await newManager.StartAsync(appStoppingToken);

                // Register BEFORE transferring ownership — if registration fails,
                // newManager is still non-null and the catch block disposes it correctly.
                await _registerAgent(newManager, appStoppingToken);

                // CAS: atomically transfer ownership.
                // If DisposeAsync ran concurrently and exchanged _hubManager to null,
                // oldManager will no longer match — the CAS fails and we dispose the orphan.
#pragma warning disable 0420 // volatile field passed by ref to Interlocked — safe by design
                if (Interlocked.CompareExchange(ref _hubManager, newManager, oldManager) != oldManager)
#pragma warning restore 0420
                {
                    await SafeDisposeAsync(newManager);
                    newManager = null;
                    // Gate was already cancelled by DisposeAsync — leave it as-is
                    return;
                }
                newManager = null; // Ownership transferred — skip disposal in catch

                // Dispose old manager after successful swap.
                await SafeDisposeAsync(oldManager);

                _logger.Information("Agent {AgentId} reconnected and re-registered after terminal close", _agentId);
                // Unblock waiters now that re-registration succeeded
                CompleteRegistrationGate();

                if (_afterSuccessfulReconnect is not null)
                    await _afterSuccessfulReconnect();

                return;
            }
            catch (OperationCanceledException) when (appStoppingToken.IsCancellationRequested)
            {
                // TODO [WARNING]: Behavioral difference from old AgentConnectionManager on this path.
                // Pre-refactor, AgentConnectionManager used CancellationToken.None for Task.Delay/StartAsync
                // and had no OCE-specific catch, so ApplicationStopping mid-reconnect fell into the generic
                // catch, logged a failure, and the loop continued (eventually completing the gate via
                // CompleteRegistrationGate and calling StopApplication on exhaustion).
                // Now, when ApplicationStopping fires, this clause returns early WITHOUT calling
                // CompleteRegistrationGate(). A concurrent caller blocked in WaitForRegistrationAsync
                // will remain blocked until either its own SignalR timeout fires or DisposeAsync cancels
                // the gate. The hang is bounded (not indefinite) and now matches AgentConnectionLifecycle
                // behaviour — confirm this deviation is intentional.
                // (Correctness review — see review-findings.md)
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
        // TODO [WARNING]: Consider TrySetCanceled() here instead of TrySetResult() so that callers
        // unblocked by the gate know the connection is terminal rather than "registered". Using
        // TrySetResult() allows callers to proceed into hub invocations on a dead connection,
        // generating noisy errors before shutdown completes. (Correctness Review)
        // Complete the gate so callers don't hang forever when giving up
        CompleteRegistrationGate();
        _lifetime?.StopApplication();
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────

    /// <summary>
    /// Cancels the registration gate (so waiters are not left hanging) and atomically
    /// disposes the underlying <see cref="IHubConnectionManager"/> exactly once via
    /// <see cref="Interlocked.Exchange{T}"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        CancelRegistrationGate(_lifetime?.ApplicationStopping ?? CancellationToken.None);

#pragma warning disable 0420 // volatile field passed by ref to Interlocked — safe by design
        var manager = Interlocked.Exchange(ref _hubManager, null);
#pragma warning restore 0420
        if (manager is null) return;
        await SafeDisposeAsync(manager);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static TaskCompletionSource CreateCompletedGate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
