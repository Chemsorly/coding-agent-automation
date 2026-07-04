namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Provides best-effort async disposal for collections of providers.
/// Each disposal is individually wrapped in try/catch so that one failing
/// DisposeAsync does not prevent subsequent providers from being disposed.
/// </summary>
public static class ProviderDisposer
{
    /// <summary>
    /// Disposes all non-null objects that implement <see cref="IAsyncDisposable"/>.
    /// Each disposal is wrapped in try/catch for resilient best-effort cleanup.
    /// </summary>
    public static async ValueTask DisposeAllAsync(params object?[] providers)
    {
        foreach (var provider in providers)
        {
            if (provider is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Disposes all non-null <see cref="IAsyncDisposable"/> items in a sequence.
    /// Each disposal is wrapped in try/catch for resilient best-effort cleanup.
    /// </summary>
    // TODO: Add null-check for `providers` parameter (ArgumentNullException.ThrowIfNull) to prevent
    // NullReferenceException from the foreach when called with null. Current call sites guard with
    // null checks, but as a public API this is fragile against future misuse.
    public static async ValueTask DisposeAllAsync(IEnumerable<IAsyncDisposable?> providers)
    {
        foreach (var provider in providers)
        {
            if (provider is null) continue;
            try { await provider.DisposeAsync(); }
            catch { /* best-effort cleanup */ }
        }
    }
}
