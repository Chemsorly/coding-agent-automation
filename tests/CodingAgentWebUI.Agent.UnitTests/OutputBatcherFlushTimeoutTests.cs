using AwesomeAssertions;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests that verify the OutputBatcher releases the lock within a bounded time
/// even when the OnFlush handler blocks (e.g., a half-open SignalR connection
/// causing InvokeAsync to hang for 60-120+ seconds).
///
/// Production scenario: 4 parallel review agents share one OutputBatcher. When the
/// flush handler hangs on a half-open TCP connection, the SemaphoreSlim lock is held
/// for the entire duration. All other producers block on _lock.WaitAsync, causing a
/// cascade that freezes the entire agent process.
///
/// The fix: OutputBatcher should accept a flush timeout. When the OnFlush callback
/// exceeds this timeout, the batcher aborts the flush and releases the lock.
/// </summary>
public class OutputBatcherFlushTimeoutTests
{
    /// <summary>
    /// When the OnFlush handler blocks beyond the configured flush timeout,
    /// subsequent AddLineAsync calls should still complete within a bounded time.
    /// Without the fix, they block for the full duration of the hung flush handler.
    /// </summary>
    [Fact]
    public async Task WhenFlushHandlerBlocks_SubsequentCallsShouldCompleteWithinBoundedTime()
    {
        var flushStarted = new TaskCompletionSource();

        // Use a short flush timeout so the test completes quickly.
        // The default constructor now applies DefaultFlushTimeout (5s), but for
        // tests we use 200ms to keep execution fast.
        await using var batcher = new OutputBatcher(flushTimeout: TimeSpan.FromMilliseconds(200));
        batcher.OnFlush += async _ =>
        {
            flushStarted.TrySetResult();
            // Simulate a half-open TCP connection: InvokeAsync hangs for a long time
            await Task.Delay(TimeSpan.FromSeconds(30));
        };

        // Fill the buffer to 49 lines
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // The 50th line triggers FlushInternalAsync which will block in OnFlush
        var triggerTask = Task.Run(async () => await batcher.AddLineAsync("trigger-flush"));

        // Wait for the blocking flush to start
        var started = await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        started.Should().Be(flushStarted.Task, "flush should start when buffer threshold is hit");

        // Now try to add another line — this caller is a parallel review agent
        // It should complete within 500ms (the flush timeout should release the lock).
        // Without the fix, this will block for ~30 seconds (the full flush duration).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var parallelTask = Task.Run(async () =>
            await batcher.AddLineAsync("parallel-agent-output", cts.Token));

        var completedInTime = await Task.WhenAny(parallelTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        completedInTime.Should().Be(parallelTask,
            "a parallel caller should not be blocked for the full duration of a hung flush handler; " +
            "the OutputBatcher should enforce a flush timeout that releases the lock");
    }

    /// <summary>
    /// Multiple parallel callers of AddLineAsync should all unblock within a bounded
    /// time even when the flush handler is hung. This simulates the production scenario
    /// where 4 parallel review agents all stall because one flush holds the lock.
    /// </summary>
    [Fact]
    public async Task WhenFlushHandlerBlocks_AllParallelCallersUnblockWithinBoundedTime()
    {
        var flushStarted = new TaskCompletionSource();

        await using var batcher = new OutputBatcher(flushTimeout: TimeSpan.FromMilliseconds(200));
        batcher.OnFlush += async _ =>
        {
            flushStarted.TrySetResult();
            // Simulate indefinitely blocked InvokeAsync on half-open connection
            await Task.Delay(TimeSpan.FromSeconds(60));
        };

        // Fill buffer to trigger threshold flush
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // 50th line triggers the blocking flush
        var triggerTask = Task.Run(async () => await batcher.AddLineAsync("trigger"));
        await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        // Simulate 3 parallel review agents trying to emit output
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var agent1 = Task.Run(async () => await batcher.AddLineAsync("agent-1", cts.Token));
        var agent2 = Task.Run(async () => await batcher.AddLineAsync("agent-2", cts.Token));
        var agent3 = Task.Run(async () => await batcher.AddLineAsync("agent-3", cts.Token));

        // All agents should complete within 500ms if flush timeout is working
        var allAgents = Task.WhenAll(agent1, agent2, agent3);
        var completed = await Task.WhenAny(allAgents, Task.Delay(TimeSpan.FromMilliseconds(500)));
        completed.Should().Be(allAgents,
            "all parallel review agents should unblock within a bounded time " +
            "when the flush handler is hung (flush timeout should release the lock)");
    }

    /// <summary>
    /// After a flush timeout fires, the batcher's periodic timer-based flush loop
    /// should continue operating normally — subsequent lines should be flushed by
    /// later timer ticks rather than being stuck permanently.
    /// </summary>
    [Fact]
    public async Task AfterFlushTimeout_TimerBasedFlushContinuesWorking()
    {
        var flushCount = 0;

        await using var batcher = new OutputBatcher(flushTimeout: TimeSpan.FromMilliseconds(200));
        batcher.OnFlush += async _ =>
        {
            var count = Interlocked.Increment(ref flushCount);
            if (count == 1)
            {
                // First flush blocks (simulates hung SignalR connection)
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            // Subsequent flushes should complete normally
        };

        // Add a line — timer will try to flush it (timer fires every 250ms)
        await batcher.AddLineAsync("first-line");

        // Wait for the first (blocking) flush to start and the timeout to fire,
        // then for subsequent timer flushes to succeed
        // With the fix: ~250ms (timer) + ~200ms (timeout) + ~250ms (next timer) ≈ 700ms
        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        // Add another line to be flushed by a subsequent timer tick
        await batcher.AddLineAsync("second-line");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // With the flush timeout fix, the timer loop should have recovered and
        // flushed the second batch. Without the fix, only 1 flush ever fires
        // (the blocking one that holds the lock forever).
        Interlocked.CompareExchange(ref flushCount, 0, 0).Should().BeGreaterThan(1,
            "after a flush timeout, the timer-based flush loop should recover and " +
            "continue flushing subsequent batches normally");
    }
}
