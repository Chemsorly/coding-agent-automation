namespace CodingAgentWebUI.Pipeline.UnitTests;

public class ProviderDisposerTests
{
    [Fact]
    public async Task DisposeAllAsync_NullProvider_Skips()
    {
        // Should not throw when null items are passed
        await ProviderDisposer.DisposeAllAsync(null, null, null);
    }

    [Fact]
    public async Task DisposeAllAsync_NonDisposable_Skips()
    {
        // Non-IAsyncDisposable objects should be silently ignored
        await ProviderDisposer.DisposeAllAsync("not disposable", 42, new object());
    }

    [Fact]
    public async Task DisposeAllAsync_DisposesAllProviders()
    {
        var d1 = new TrackingDisposable();
        var d2 = new TrackingDisposable();
        var d3 = new TrackingDisposable();

        await ProviderDisposer.DisposeAllAsync(d1, d2, d3);

        Assert.True(d1.Disposed);
        Assert.True(d2.Disposed);
        Assert.True(d3.Disposed);
    }

    [Fact]
    public async Task DisposeAllAsync_OneThrows_StillDisposesRemaining()
    {
        var d1 = new TrackingDisposable();
        var throwing = new ThrowingDisposable();
        var d3 = new TrackingDisposable();

        await ProviderDisposer.DisposeAllAsync(d1, throwing, d3);

        // TODO: Assert that ThrowingDisposable.DisposeAsync() was actually invoked (add a
        // DisposeAttempted tracking flag) to confirm the exception path was exercised, not
        // that the throwing item was simply skipped by a bug.
        Assert.True(d1.Disposed);
        Assert.True(d3.Disposed);
    }

    [Fact]
    public async Task DisposeAllAsync_EmptyArray_NoOp()
    {
        await ProviderDisposer.DisposeAllAsync();
    }

    [Fact]
    public async Task DisposeAllAsync_Enumerable_DisposesAll()
    {
        var d1 = new TrackingDisposable();
        var d2 = new TrackingDisposable();

        IEnumerable<IAsyncDisposable?> items = [d1, d2];
        await ProviderDisposer.DisposeAllAsync(items);

        Assert.True(d1.Disposed);
        Assert.True(d2.Disposed);
    }

    [Fact]
    public async Task DisposeAllAsync_Enumerable_NullItems_Skipped()
    {
        var d1 = new TrackingDisposable();

        IEnumerable<IAsyncDisposable?> items = [null, d1, null];
        await ProviderDisposer.DisposeAllAsync(items);

        Assert.True(d1.Disposed);
    }

    [Fact]
    public async Task DisposeAllAsync_Enumerable_OneThrows_StillDisposesRemaining()
    {
        var d1 = new TrackingDisposable();
        var throwing = new ThrowingDisposable();
        var d3 = new TrackingDisposable();

        IEnumerable<IAsyncDisposable?> items = [d1, throwing, d3];
        await ProviderDisposer.DisposeAllAsync(items);

        Assert.True(d1.Disposed);
        Assert.True(d3.Disposed);
    }

    [Fact]
    public async Task DisposeAllAsync_MixedNullAndDisposable()
    {
        var d1 = new TrackingDisposable();

        await ProviderDisposer.DisposeAllAsync(null, d1, "not disposable", null);

        Assert.True(d1.Disposed);
    }

    private sealed class TrackingDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Disposal failed");
    }
}
