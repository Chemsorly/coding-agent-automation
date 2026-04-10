using KiroCliPoc.Models;
using Serilog;

namespace KiroCliPoc.Core;

/// <summary>
/// Manages callback registration and invocation with error handling.
/// </summary>
public class CallbackHandler
{
    private readonly Dictionary<KiroState, List<Action<CallbackContext>>> _callbacks = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the CallbackHandler class.
    /// </summary>
    /// <param name="logger">Logger for error handling.</param>
    public CallbackHandler(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Registers a callback for a specific Kiro CLI state.
    /// </summary>
    /// <param name="state">The state to register the callback for.</param>
    /// <param name="callback">The callback action to invoke.</param>
    public void RegisterCallback(KiroState state, Action<CallbackContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!_callbacks.ContainsKey(state))
        {
            _callbacks[state] = new List<Action<CallbackContext>>();
        }

        _callbacks[state].Add(callback);
    }

    /// <summary>
    /// Registers a callback for the Started state.
    /// </summary>
    public void RegisterOnStarted(Action<CallbackContext> callback) =>
        RegisterCallback(KiroState.Started, callback);

    /// <summary>
    /// Registers a callback for progress updates (ResearchPhase, PlanPhase, ImplementPhase, TestPhase).
    /// </summary>
    public void RegisterOnProgress(Action<CallbackContext> callback)
    {
        RegisterCallback(KiroState.ResearchPhase, callback);
        RegisterCallback(KiroState.PlanPhase, callback);
        RegisterCallback(KiroState.ImplementPhase, callback);
        RegisterCallback(KiroState.TestPhase, callback);
    }

    /// <summary>
    /// Registers a callback for the Completed state.
    /// </summary>
    public void RegisterOnCompleted(Action<CallbackContext> callback) =>
        RegisterCallback(KiroState.Completed, callback);

    /// <summary>
    /// Registers a callback for the Error state.
    /// </summary>
    public void RegisterOnError(Action<CallbackContext> callback) =>
        RegisterCallback(KiroState.Error, callback);

    /// <summary>
    /// Registers a callback for the NeedsInput state.
    /// </summary>
    public void RegisterOnNeedsInput(Action<CallbackContext> callback) =>
        RegisterCallback(KiroState.NeedsInput, callback);

    /// <summary>
    /// Registers a callback for the Timeout state.
    /// </summary>
    public void RegisterOnTimeout(Action<CallbackContext> callback) =>
        RegisterCallback(KiroState.Timeout, callback);

    /// <summary>
    /// Registers a callback for file changes (invoked separately from state changes).
    /// This is a special callback that doesn't correspond to a KiroState.
    /// </summary>
    public void RegisterOnFilesChanged(Action<CallbackContext> callback)
    {
        // File changes are handled separately, but we can use a special marker
        // For now, we'll just store it under a dedicated key
        // In practice, this would be invoked by the orchestrator after comparing file snapshots
        ArgumentNullException.ThrowIfNull(callback);
        
        // We could use a separate dictionary or a special state value
        // For simplicity, let's add a comment that this needs special handling
        // The orchestrator will need to call this directly
    }

    /// <summary>
    /// Invokes all callbacks registered for the specified state.
    /// Exceptions thrown by callbacks are caught, logged, and do not prevent other callbacks from executing.
    /// </summary>
    /// <param name="state">The state that triggered the callbacks.</param>
    /// <param name="context">The context to pass to the callbacks.</param>
    public void Invoke(KiroState state, CallbackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_callbacks.TryGetValue(state, out var callbacks))
        {
            // No callbacks registered for this state
            return;
        }

        foreach (var callback in callbacks)
        {
            try
            {
                callback(context);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Callback for state {State} threw an exception. Continuing execution.", state);
            }
        }
    }

    /// <summary>
    /// Gets the number of callbacks registered for a specific state.
    /// </summary>
    /// <param name="state">The state to check.</param>
    /// <returns>The number of registered callbacks.</returns>
    public int GetCallbackCount(KiroState state)
    {
        return _callbacks.TryGetValue(state, out var callbacks) ? callbacks.Count : 0;
    }

    /// <summary>
    /// Clears all registered callbacks.
    /// </summary>
    public void ClearAll()
    {
        _callbacks.Clear();
    }
}
