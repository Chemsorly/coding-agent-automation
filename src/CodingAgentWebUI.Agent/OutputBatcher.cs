namespace CodingAgentWebUI.Agent;

/// <summary>
/// Batches output lines to reduce SignalR invocation frequency.
/// Flushes every 250ms or every 50 lines, whichever comes first.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flush Timeout:</b> When <c>flushTimeout</c> is provided, the <see cref="OnFlush"/>
/// callback is abandoned if it exceeds the timeout. This prevents a blocking flush handler
/// (e.g., a hung SignalR InvokeAsync on a half-open TCP connection) from holding the
/// <see cref="SemaphoreSlim"/> indefinitely and starving all concurrent callers of
/// <see cref="AddLineAsync"/>. Batched lines are discarded on timeout (best-effort delivery).
/// </para>
/// </remarks>
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
    private readonly TimeSpan _flushTimeout;

    private const int MaxBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Default flush timeout: 5 seconds. Bounds how long the lock is held during
    /// a single flush operation. If the OnFlush handler exceeds this, the flush is
    /// abandoned and the lock released to prevent cascading stalls.
    /// </summary>
    public static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fired when a batch of lines is ready to be sent.
    /// </summary>
    public event Func<IReadOnlyList<string>, Task>? OnFlush;

    /// <summary>
    /// Creates a new OutputBatcher with optional flush timeout.
    /// </summary>
    /// <param name="flushTimeout">
    /// Maximum duration for the <see cref="OnFlush"/> callback before it is abandoned.
    /// Defaults to <see cref="DefaultFlushTimeout"/> (5 seconds). Use <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable the timeout (legacy behavior, not recommended for production).
    /// </param>
    public OutputBatcher(TimeSpan? flushTimeout = null)
    {
        _flushTimeout = flushTimeout ?? DefaultFlushTimeout;
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
    /// If the flush handler exceeds <see cref="_flushTimeout"/>, it is abandoned to
    /// release the lock and prevent cascading stalls.
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
                if (_flushTimeout == Timeout.InfiniteTimeSpan)
                {
                    await OnFlush(batch);
                }
                else
                {
                    using var flushCts = new CancellationTokenSource(_flushTimeout);
                    var flushTask = OnFlush(batch);
                    var timeoutTask = Task.Delay(Timeout.Infinite, flushCts.Token);
                    var completed = await Task.WhenAny(flushTask, timeoutTask);

                    if (completed == flushTask)
                    {
                        // Propagate exception if the flush faulted
                        await flushTask;
                    }
                    // else: flush timed out — abandon it and release the lock (best-effort)
                }
            }
            catch
            {
                // Best-effort delivery — don't crash the batcher if the handler fails or times out
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();

        try { await _flushLoop; }
        catch (OperationCanceledException) { }

        // Flush any remaining lines (with timeout to prevent hang during disposal)
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
