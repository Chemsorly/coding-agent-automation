namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Controls when the <see cref="OutputBatcher"/> flush loop wakes up.
/// Production: real <see cref="PeriodicTimer"/>. Tests: <see cref="ManualFlushTrigger"/>.
/// </summary>
public interface IFlushTrigger : IAsyncDisposable
{
    /// <summary>
    /// Wait until the next flush tick. Returns <c>false</c> when the trigger is stopped.
    /// </summary>
    ValueTask<bool> WaitForNextTickAsync(CancellationToken ct);
}

/// <summary>
/// Production implementation — wraps <see cref="PeriodicTimer"/>.
/// </summary>
internal sealed class PeriodicTimerFlushTrigger : IFlushTrigger
{
    private readonly PeriodicTimer _timer;

    public PeriodicTimerFlushTrigger(TimeSpan interval) => _timer = new PeriodicTimer(interval);

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
        => _timer.WaitForNextTickAsync(ct);

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Test implementation — each call to <see cref="Tick"/> unblocks one
/// <see cref="WaitForNextTickAsync"/> waiter. No real time passes.
/// Call <see cref="Stop"/> to make the flush loop exit cleanly.
/// </summary>
public sealed class ManualFlushTrigger : IFlushTrigger
{
    // Each Tick() releases one permit; WaitForNextTickAsync() consumes one.
    private readonly SemaphoreSlim _gate = new(0);
    private volatile bool _stopped;

    /// <summary>Unblocks one pending <see cref="WaitForNextTickAsync"/> call.</summary>
    public void Tick() => _gate.Release();

    /// <summary>Causes all future and current <see cref="WaitForNextTickAsync"/> calls to return false.</summary>
    public void Stop()
    {
        _stopped = true;
        _gate.Release(); // wake any blocked waiter so it can observe _stopped
    }

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return !_stopped;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Batches output lines to reduce SignalR invocation frequency.
/// Flushes on every trigger tick or every 50 lines, whichever comes first.
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
/// <para>
/// <b>Testability:</b> Pass a <see cref="ManualFlushTrigger"/> to control when timer ticks
/// fire without any real-time delays.
/// </para>
/// </remarks>
public sealed class OutputBatcher : IAsyncDisposable
{
    private readonly List<string> _buffer = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly IFlushTrigger _trigger;
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _flushTimeout;

    private const int MaxBatchSize = 50;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(250);

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
    /// Creates an <see cref="OutputBatcher"/> with the real periodic timer (production path).
    /// </summary>
    /// <param name="flushTimeout">
    /// Maximum duration for the <see cref="OnFlush"/> callback before it is abandoned.
    /// Defaults to <see cref="DefaultFlushTimeout"/> (5 seconds). Use <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable the timeout (legacy behavior, not recommended for production).
    /// </param>
    public OutputBatcher(TimeSpan? flushTimeout = null)
        : this(new PeriodicTimerFlushTrigger(DefaultFlushInterval), flushTimeout)
    {
    }

    /// <summary>
    /// Creates an <see cref="OutputBatcher"/> with an explicit flush trigger.
    /// Use <see cref="ManualFlushTrigger"/> in tests to avoid real-time waits.
    /// </summary>
    public OutputBatcher(IFlushTrigger trigger, TimeSpan? flushTimeout = null)
    {
        _trigger = trigger;
        _flushTimeout = flushTimeout ?? DefaultFlushTimeout;
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
            while (await _trigger.WaitForNextTickAsync(_cts.Token))
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
        await _trigger.DisposeAsync();

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
