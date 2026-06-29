namespace CodingAgentWebUI.Infrastructure.Locking;

/// <summary>
/// Abstracts distributed locking for operations that must be serialized
/// across multiple application instances (e.g., schema migration, config migration).
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>
    /// Acquires a named lock. The lock is held until the returned IAsyncDisposable is disposed.
    /// Blocks until the lock is acquired or cancellation is requested.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default);
}
