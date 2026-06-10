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
/// (e.g., a hung SignalR InvokeAsync on a half-open TCP connection) from stalling delivery
/// of subsequent batches. Batched lines are discarded on timeout (best-effort delivery).
/// </para>
/// <para>
/// <b>Lock Design:</b> The buffer lock (<c>_lock</c>) is held only during buffer add/copy/clear
/// operations (microseconds). The flush gate (<c>_flushGate</c>) serializes <see cref="OnFlush"/>
/// invocations to preserve batch ordering without blocking <see cref="AddLineAsync"/> callers.
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
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly PeriodicTimer _timer;
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _flushTimeout;

    private const int MaxBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Default flush timeout: 5 seconds. Bounds how long the flush gate is held during
    /// a single send operation. If the OnFlush handler exceeds this, the flush is
    /// abandoned to prevent cascading delivery delays.
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
    /// Never blocks on network I/O — the send happens outside the buffer lock.
    /// </summary>
    public async Task AddLineAsync(string line, CancellationToken ct = default)
    {
        List<string>? batch = null;

        await _lock.WaitAsync(ct);
        try
        {
            _buffer.Add(line);
            if (_buffer.Count >= MaxBatchSize)
            {
                batch = _buffer.ToList();
                _buffer.Clear();
            }
        }
        finally
        {
            _lock.Release();
        }

        if (batch is not null)
            await SendBatchAsync(batch);
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                List<string>? batch = null;

                await _lock.WaitAsync(_cts.Token);
                try
                {
                    if (_buffer.Count > 0)
                    {
                        batch = _buffer.ToList();
                        _buffer.Clear();
                    }
                }
                finally
                {
                    _lock.Release();
                }

                if (batch is not null)
                    await SendBatchAsync(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal
        }
    }

    /// <summary>
    /// Sends a batch via <see cref="OnFlush"/>, serialized by <see cref="_flushGate"/> to
    /// preserve ordering. If the handler exceeds <see cref="_flushTimeout"/>, it is abandoned
    /// (best-effort delivery).
    /// </summary>
    private async Task SendBatchAsync(List<string> batch)
    {
        await _flushGate.WaitAsync();
        try
        {
            if (OnFlush is not null)
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
                    // else: flush timed out — abandon it (best-effort)
                }
            }
        }
        catch
        {
            // Best-effort delivery — don't crash the batcher if the handler fails or times out
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();

        try { await _flushLoop; }
        catch (OperationCanceledException) { }

        // Final flush of remaining lines
        List<string>? batch = null;
        await _lock.WaitAsync();
        try
        {
            if (_buffer.Count > 0)
            {
                batch = _buffer.ToList();
                _buffer.Clear();
            }
        }
        finally
        {
            _lock.Release();
        }

        if (batch is not null)
            await SendBatchAsync(batch);

        _lock.Dispose();
        _flushGate.Dispose();
        _cts.Dispose();
    }
}
