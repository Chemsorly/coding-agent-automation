using KiroCliLib.Models;
using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Manages callback registration and invocation with error isolation.
/// </summary>
public class CallbackHandler
{
    private readonly Dictionary<KiroState, List<Action<CallbackContext>>> _callbacks = new();
    private readonly ILogger _logger;

    public CallbackHandler(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void RegisterCallback(KiroState state, Action<CallbackContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_callbacks.ContainsKey(state))
            _callbacks[state] = new List<Action<CallbackContext>>();
        _callbacks[state].Add(callback);
    }

    public void RegisterOnStarted(Action<CallbackContext> callback) => RegisterCallback(KiroState.Started, callback);
    public void RegisterOnCompleted(Action<CallbackContext> callback) => RegisterCallback(KiroState.Completed, callback);
    public void RegisterOnError(Action<CallbackContext> callback) => RegisterCallback(KiroState.Error, callback);
    public void RegisterOnNeedsInput(Action<CallbackContext> callback) => RegisterCallback(KiroState.NeedsInput, callback);
    public void RegisterOnTimeout(Action<CallbackContext> callback) => RegisterCallback(KiroState.Timeout, callback);

    public void RegisterOnProgress(Action<CallbackContext> callback)
    {
        RegisterCallback(KiroState.ResearchPhase, callback);
        RegisterCallback(KiroState.PlanPhase, callback);
        RegisterCallback(KiroState.ImplementPhase, callback);
        RegisterCallback(KiroState.TestPhase, callback);
    }

    public void Invoke(KiroState state, CallbackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_callbacks.TryGetValue(state, out var callbacks)) return;

        foreach (var callback in callbacks)
        {
            try { callback(context); }
            catch (Exception ex) { _logger.Error(ex, "Callback for state {State} threw an exception.", state); }
        }
    }

    public int GetCallbackCount(KiroState state) =>
        _callbacks.TryGetValue(state, out var callbacks) ? callbacks.Count : 0;

    public void ClearAll() => _callbacks.Clear();
}
