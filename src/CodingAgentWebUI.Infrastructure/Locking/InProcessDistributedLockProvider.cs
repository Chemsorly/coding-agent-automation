using System.Collections.Concurrent;

namespace CodingAgentWebUI.Infrastructure.Locking;

/// <summary>
/// In-process distributed lock using ConcurrentDictionary + SemaphoreSlim(1,1).
/// Suitable for single-instance deployments and SQLite/no-DB scenarios.
/// </summary>
internal sealed class InProcessDistributedLockProvider : IDistributedLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        var semaphore = _locks.GetOrAdd(lockName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new SemaphoreHandle(semaphore);
    }

    private sealed class SemaphoreHandle : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public SemaphoreHandle(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
