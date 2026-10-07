using AwesomeAssertions;
using CodingAgent.Infrastructure;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests verifying that OutputBatcher does NOT block AddLineAsync callers during
/// the OnFlush callback (which performs async network I/O). The buffer lock is released
/// before the send, so concurrent producers are never stalled by a slow flush handler.
/// </summary>
public class OutputBatcherSignalRBlockingTests
{
    /// <summary>
    /// Verifies that when OnFlush blocks (simulating a SignalR invocation waiting for
    /// reconnection), subsequent AddLineAsync calls complete immediately because the
    /// buffer lock is released before the send phase.
    /// </summary>
    [Fact]
    public async Task WhenOnFlushBlocks_AddLineAsyncIsNotBlocked()
    {
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A flush timeout longer than the 30s hang detectors below keeps the blocked flush in the
        // send phase for the whole test, so only the lock design can let the agents through.
        await using var batcher = new OutputBatcher(flushTimeout: TimeSpan.FromSeconds(60));
        batcher.OnFlush += async _ =>
        {
            flushStarted.TrySetResult();
            // Simulate SignalR InvokeAsync blocking during reconnection
            await flushCanComplete.Task;
        };

        // Add 49 lines (won't trigger threshold flush yet)
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // The 50th line triggers flush which will block in OnFlush.
        var addLine50Task = Task.Run(async () => await batcher.AddLineAsync("line-49"));

        // Wait for the flush to start (proves the send phase has begun). The 30s bounds in this
        // test are hang detectors only: a stalled test host must not fail it.
        var flushStartedInTime = await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        flushStartedInTime.Should().Be(flushStarted.Task, "flush should start when 50th line is added");

        // Parallel agents adding output lines should complete immediately
        // because the buffer lock is released before the send phase.
        var parallelAgent1 = Task.Run(async () =>
            await batcher.AddLineAsync("agent-1-output"));
        var parallelAgent2 = Task.Run(async () =>
            await batcher.AddLineAsync("agent-2-output"));
        var parallelAgent3 = Task.Run(async () =>
            await batcher.AddLineAsync("agent-3-output"));

        // All agents should complete quickly (not blocked by the flush)
        var allAgents = Task.WhenAll(parallelAgent1, parallelAgent2, parallelAgent3);
        var completed = await Task.WhenAny(allAgents, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().Be(allAgents,
            "all agents should complete immediately — AddLineAsync does not block on network I/O");

        // Cleanup
        flushCanComplete.SetResult();
        await addLine50Task;
    }

    /// <summary>
    /// Verifies that when the flush handler blocks, the timer-based flush loop can still
    /// extract batches from the buffer. The timer flush will queue behind the flush gate
    /// for sending, but buffer extraction is not stalled.
    /// </summary>
    [Fact]
    public async Task WhenOnFlushBlocks_TimerBasedFlushStillOperates()
    {
        var flushStarted = new TaskCompletionSource();
        var flushCanComplete = new TaskCompletionSource();
        var flushCount = 0;
        var secondFlushFired = new TaskCompletionSource();

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
            else
            {
                // Second (or later) flush — signal test completion
                secondFlushFired.TrySetResult();
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

        // Add more lines — these should be accepted immediately (lock is free)
        await batcher.AddLineAsync("extra-1");

        // Unblock the first flush
        flushCanComplete.SetResult();
        await triggerTask;

        // Wait deterministically for the timer-based flush to fire (up to 5s)
        var secondFired = await Task.WhenAny(secondFlushFired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        secondFired.Should().Be(secondFlushFired.Task,
            "timer-based flushes should operate independently of a blocked send");

        // Verify at least two flushes occurred
        Interlocked.CompareExchange(ref flushCount, 0, 0).Should().BeGreaterThan(1,
            "both threshold flush and timer flush should have fired");
    }

    /// <summary>
    /// Verifies that the flush timeout mechanism prevents indefinite blocking
    /// of the send phase, allowing subsequent batches to be delivered.
    /// </summary>
    [Fact]
    public async Task FlushHandlerWithTimeout_PreventsIndefiniteBlocking()
    {
        var flushTimedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var batcher = new OutputBatcher();
        batcher.OnFlush += async _ =>
        {
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
                flushTimedOut.TrySetResult();
            }
        };

        // Trigger a flush
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Verify: the flush was attempted and timed out. Event-driven — the flush may run on the
        // timer path rather than inline — and the 30s bounds in this test are hang detectors only.
        var timedOut = await Task.WhenAny(flushTimedOut.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        timedOut.Should().Be(flushTimedOut.Task,
            "the flush timeout should fire, preventing indefinite lock hold");

        // Prove that subsequent AddLineAsync calls are NOT blocked
        var addTask = batcher.AddLineAsync("after-timeout-line");
        var completed = await Task.WhenAny(addTask, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().Be(addTask,
            "AddLineAsync should complete quickly after the timed-out flush releases the lock");
    }
}
