using CodingAgentWebUI.Infrastructure.Locking;

namespace CodingAgentWebUI.IntegrationTests.Helpers;

/// <summary>
/// No-op lock provider for InMemory EF Core tests.
/// Always grants the lock immediately (no contention in single-threaded tests).
/// </summary>
internal sealed class NoOpDistributedLockProvider : IDistributedLockProvider
{
    public Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable>(NoOpHandle.Instance);

    private sealed class NoOpHandle : IAsyncDisposable
    {
        public static readonly NoOpHandle Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
