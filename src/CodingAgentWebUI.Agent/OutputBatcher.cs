namespace CodingAgentWebUI.Agent;

/// <summary>
/// Batches output lines to reduce SignalR invocation frequency.
/// Flushes every 250ms or every 50 lines, whichever comes first.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
// TODO: Extraction candidate for shared library. OutputBatcher is a general-purpose
// async batching utility (configurable interval + max batch size) with no dependency on
// SignalR or agent-specific types. If other consumers emerge (e.g., KiroCliLib output
// buffering), extract to a shared library with zero external dependencies. The core
// algorithm (timer-based flush + size-based flush + thread-safe buffer) is reusable as-is.
public sealed class OutputBatcher : IAsyncDisposable
{
    private readonly List<string> _buffer = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly PeriodicTimer _timer;
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _cts = new();

    private const int MaxBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fired when a batch of lines is ready to be sent.
    /// </summary>
    public event Func<IReadOnlyList<string>, Task>? OnFlush;

    public OutputBatcher()
    {
        _timer = new PeriodicTimer(FlushInterval);
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>
    /// Adds a line to the buffer. Auto-flushes when the buffer reaches <see cref="MaxBatchSize"/>.
    /// </summary>
    public async Task AddLineAsync(string line, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _buffer.Add(line);
            if (_buffer.Count >= MaxBatchSize)
                await FlushInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                await _lock.WaitAsync(_cts.Token);
                try
                {
                    if (_buffer.Count > 0)
                        await FlushInternalAsync();
                }
                finally
                {
                    _lock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal
        }
    }

    /// <summary>
    /// Must be called while holding <see cref="_lock"/>.
    /// Sends the current buffer contents via <see cref="OnFlush"/> and clears the buffer.
    /// </summary>
    private async Task FlushInternalAsync()
    {
        if (_buffer.Count == 0) return;

        var batch = _buffer.ToList();
        _buffer.Clear();

        if (OnFlush is not null)
        {
            try
            {
                await OnFlush(batch);
            }
            catch
            {
                // Best-effort delivery — don't crash the batcher if the handler fails
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();

        try { await _flushLoop; }
        catch (OperationCanceledException) { }

        // Flush any remaining lines
        await _lock.WaitAsync();
        try
        {
            if (_buffer.Count > 0)
                await FlushInternalAsync();
        }
        finally
        {
            _lock.Release();
        }

        _lock.Dispose();
        _cts.Dispose();
    }
}
