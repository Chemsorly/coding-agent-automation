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

    public void RegisterOnCompleted(Action<CallbackContext> callback) => RegisterCallback(KiroState.Completed, callback);

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
}
