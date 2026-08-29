using Microsoft.JSInterop;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Pushes frontend observability signals to Grafana Faro via JS interop.
/// All methods are fire-and-forget safe: JS errors (Faro not loaded, circuit disconnected,
/// CDN blocked) are swallowed so callers never need to guard against monitoring failures.
/// </summary>
public interface IFaroService
{
    /// <summary>Sends a structured log entry to Faro.</summary>
    Task PushLogAsync(string message, string level = "info");

    /// <summary>Sends an error (with optional stack trace) to Faro.</summary>
    Task PushErrorAsync(string message, string? stack = null);

    /// <summary>Sends a named event with optional key-value attributes to Faro.</summary>
    Task PushEventAsync(string name, IDictionary<string, string>? attributes = null);
}

/// <inheritdoc />
internal sealed class FaroService(IJSRuntime js) : IFaroService
{
    public async Task PushLogAsync(string message, string level = "info")
    {
        try
        {
            await js.InvokeVoidAsync("faroApi.pushLog", message, level);
        }
        catch (Exception ex) when (IsSafeToSwallow(ex)) { }
    }

    public async Task PushErrorAsync(string message, string? stack = null)
    {
        try
        {
            await js.InvokeVoidAsync("faroApi.pushError", message, stack);
        }
        catch (Exception ex) when (IsSafeToSwallow(ex)) { }
    }

    public async Task PushEventAsync(string name, IDictionary<string, string>? attributes = null)
    {
        try
        {
            await js.InvokeVoidAsync("faroApi.pushEvent", name, attributes);
        }
        catch (Exception ex) when (IsSafeToSwallow(ex)) { }
    }

    /// <summary>
    /// Returns true for exceptions that indicate Faro is unavailable (CDN blocked, circuit
    /// disconnected, component disposed) rather than a bug in the calling code.
    /// OperationCanceledException (parent of TaskCanceledException) is used rather than
    /// TaskCanceledException directly — IJSRuntime throws either depending on the runtime
    /// version and cancellation path. Consistent with the rest of the codebase.
    /// </summary>
    private static bool IsSafeToSwallow(Exception ex) =>
        ex is JSException
            or JSDisconnectedException
            or OperationCanceledException
            or ObjectDisposedException
            or InvalidOperationException;
}
