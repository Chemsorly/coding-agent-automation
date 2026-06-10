using AwesomeAssertions;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests demonstrating that OutputBatcher holds its SemaphoreSlim lock for the entire
/// duration of the OnFlush callback. If SignalR invocations block during reconnection,
/// all concurrent callers of AddLineAsync are blocked too — including parallel review
/// agents and the TimeoutHelper's CancelAfter timer callback (which needs a thread pool
/// thread to fire).
///
/// Production scenario: 4 parallel review agents share one OutputBatcher. When SignalR
/// disconnects briefly, InvokeAsync calls inside OnFlush either throw quickly (good case)
/// or block while waiting for reconnection (bad case). During the blocking window, any
/// agent producing output gets stuck on _lock.WaitAsync, and because the lock holder
/// awaits the OnFlush handler, all producers stall.
///
/// This is a contributing factor to the 6-hour hang: if the flush handler blocks long
/// enough during reconnection, it can cause a cascade where all parallel Tasks are parked
/// waiting for the lock, thread pool threads are exhausted, and timer-based cancellation
/// (CancellationTokenSource.CancelAfter) never fires because its callback can't be
/// scheduled on the saturated thread pool.
/// </summary>
public class OutputBatcherSignalRBlockingTests
{
    /// <summary>
    /// Demonstrates that when OnFlush blocks (simulating a SignalR invocation waiting for
    /// reconnection), subsequent AddLineAsync calls are blocked because the SemaphoreSlim
    /// is held during the entire flush operation.
    ///
    /// This is the mechanism by which a SignalR disconnect can freeze all parallel review
    /// agents: they all call EmitOutputLine → AddLineAsync → wait on _lock → _lock is
    /// held by the flush loop waiting on a blocked InvokeAsync.
    /// </summary>
    [Fact]
    public async Task WhenOnFlushBlocks_AddLineAsyncIsBlockedForAllCallers()
    {
        var flushStarted = new TaskCompletionSource();
        var flushCanComplete = new TaskCompletionSource();

        await using var batcher = new OutputBatcher();
        batcher.OnFlush += async _ =>
        {
            flushStarted.TrySetResult();
            // Simulate SignalR InvokeAsync blocking during reconnection
            await flushCanComplete.Task;
        };

        // Add 49 lines (won't trigger threshold flush yet)
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // The 50th line triggers FlushInternalAsync which will block in OnFlush.
        // Must be in Task.Run since it won't return until flush completes.
        var addLine50Task = Task.Run(async () => await batcher.AddLineAsync("line-49"));

        // Wait for the flush to start (proves the lock is now held by addLine50Task)
        var flushStartedInTime = await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        flushStartedInTime.Should().Be(flushStarted.Task, "flush should start when 50th line is added");

        // Now simulate parallel review agents trying to add output lines.
        // They should all block on _lock.WaitAsync because the flush holds the lock.
        using var blockedCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var parallelAgent1 = Task.Run(async () =>
            await batcher.AddLineAsync("agent-1-output", blockedCts.Token));
        var parallelAgent2 = Task.Run(async () =>
            await batcher.AddLineAsync("agent-2-output", blockedCts.Token));
        var parallelAgent3 = Task.Run(async () =>
            await batcher.AddLineAsync("agent-3-output", blockedCts.Token));

        // Give the agents time to reach the lock wait
        await Task.Delay(200);

        // All agents should still be blocked (not completed)
        parallelAgent1.IsCompleted.Should().BeFalse("agent 1 should be blocked waiting on the lock");
        parallelAgent2.IsCompleted.Should().BeFalse("agent 2 should be blocked waiting on the lock");
        parallelAgent3.IsCompleted.Should().BeFalse("agent 3 should be blocked waiting on the lock");

        // Now let the flush complete (simulating SignalR reconnection completing)
        flushCanComplete.SetResult();

        // The original addLine50 should now complete
        await addLine50Task;

        // All blocked agents should now proceed
        var allAgents = Task.WhenAll(parallelAgent1, parallelAgent2, parallelAgent3);
        var completed = await Task.WhenAny(allAgents, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(allAgents, "all agents should unblock after flush completes");
    }

    /// <summary>
    /// Demonstrates that the timer-based flush loop also acquires the lock, meaning if the
    /// lock is held by a blocking flush, the periodic timer ticks accumulate without
    /// processing — effectively disabling the batcher's time-based flush mechanism.
    ///
    /// This is relevant because: if one parallel agent's batch-size flush blocks,
    /// the timer-based flush for OTHER agents' output also stops, preventing any output
    /// from being delivered even if those other agents have pending lines.
    /// </summary>
    [Fact]
    public async Task WhenFlushBlocks_TimerBasedFlushAlsoStalls()
    {
        var flushStarted = new TaskCompletionSource();
        var flushCanComplete = new TaskCompletionSource();
        var flushCount = 0;

        await using var batcher = new OutputBatcher();
        batcher.OnFlush += async _ =>
        {
            var count = Interlocked.Increment(ref flushCount);
            if (count == 1)
            {
                // First flush blocks (simulating SignalR issue)
                flushStarted.TrySetResult();
                await flushCanComplete.Task;
            }
        };

        // Add 49 lines then trigger the 50th in a background task
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // 50th line triggers threshold flush — blocks in OnFlush
        var triggerTask = Task.Run(async () => await batcher.AddLineAsync("line-49"));

        // Wait for the blocking flush to start
        var started = await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        started.Should().Be(flushStarted.Task, "flush should start");

        // Wait for timer ticks to accumulate (500ms = ~2 timer ticks at 250ms interval)
        // The timer loop is also blocked on _lock.WaitAsync
        await Task.Delay(600);

        // Only 1 flush should have occurred (the blocked one)
        // Timer-based flushes are stalled behind the lock
        Interlocked.CompareExchange(ref flushCount, 0, 0).Should().Be(1,
            "timer-based flushes should be blocked while the lock is held by a blocking flush");

        // Unblock
        flushCanComplete.SetResult();
        await triggerTask;

        // Add more lines and wait for timer to flush them
        await batcher.AddLineAsync("extra-1");
        await Task.Delay(500); // let timer catch up

        // Now subsequent flushes should have fired
        Interlocked.CompareExchange(ref flushCount, 0, 0).Should().BeGreaterThan(1,
            "timer-based flushes should resume after the blocking flush completes");
    }

    /// <summary>
    /// Demonstrates that the OnFlush handler in AgentWorkerService uses a raw InvokeAsync
    /// without any timeout or CancellationToken. If InvokeAsync hangs (unlikely but possible
    /// during certain connection states), the flush holds the lock indefinitely.
    ///
    /// This test proves that adding a CancellationToken with a timeout to the OnFlush handler
    /// would prevent the cascade failure. The pattern should be:
    ///   using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    ///   await connection.InvokeAsync("ReportOutputLines", jobId, lines, cts.Token);
    ///
    /// Without this, a single stuck InvokeAsync can freeze the entire agent worker.
    /// </summary>
    [Fact]
    public async Task FlushHandlerWithTimeout_PreventsIndefiniteBlocking()
    {
        var flushCount = 0;
        var timedOutFlushes = 0;

        await using var batcher = new OutputBatcher();
        batcher.OnFlush += async _ =>
        {
            Interlocked.Increment(ref flushCount);

            // Simulate the FIXED pattern: OnFlush with a timeout
            using var flushCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                // Simulate a blocking SignalR call that would normally hang
                await Task.Delay(TimeSpan.FromSeconds(10), flushCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout fired — flush fails fast instead of blocking indefinitely
                Interlocked.Increment(ref timedOutFlushes);
            }
        };

        // Trigger a flush
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Give time for the timeout to fire
        await Task.Delay(300);

        // Verify: the flush was attempted and timed out quickly
        Interlocked.CompareExchange(ref flushCount, 0, 0).Should().BeGreaterThanOrEqualTo(1);
        Interlocked.CompareExchange(ref timedOutFlushes, 0, 0).Should().BeGreaterThanOrEqualTo(1,
            "the flush timeout should fire, preventing indefinite lock hold");

        // Prove that subsequent AddLineAsync calls are NOT blocked
        var addTask = batcher.AddLineAsync("after-timeout-line");
        var completed = await Task.WhenAny(addTask, Task.Delay(TimeSpan.FromSeconds(1)));
        completed.Should().Be(addTask,
            "AddLineAsync should complete quickly after the timed-out flush releases the lock");
    }
}
