using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;

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
/// All tests use <see cref="ManualFlushTrigger"/> so no real-time delays are needed —
/// the flush loop only wakes when Tick() is called explicitly.
/// </summary>
public class OutputBatcherFlushTimeoutTests
{
    /// <summary>
    /// When the OnFlush handler blocks beyond the configured flush timeout,
    /// subsequent AddLineAsync calls should still complete within a bounded time.
    /// Without the fix, they block for the full duration of the hung flush handler.
    ///
    /// The threshold flush (50 lines) is used to trigger the blocking handler —
    /// no timer tick required.
    /// </summary>
    [Fact]
    public async Task WhenFlushHandlerBlocks_SubsequentCallsShouldCompleteWithinBoundedTime()
    {
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trigger = new ManualFlushTrigger();

        await using var batcher = new OutputBatcher(trigger, flushTimeout: TimeSpan.FromMilliseconds(200));
        batcher.OnFlush += async _ =>
        {
            flushStarted.TrySetResult();
            // Simulate a half-open TCP connection: InvokeAsync hangs indefinitely
            await Task.Delay(TimeSpan.FromSeconds(30));
        };

        // Fill the buffer to 49 lines — no flush yet
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // The 50th line crosses the threshold and fires SendBatchAsync synchronously
        var triggerTask = Task.Run(async () => await batcher.AddLineAsync("trigger-flush"));

        // Wait for the blocking flush to start — no real-time dependency, just
        // waiting for the threshold-triggered flush to reach OnFlush
        var started = await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        started.Should().Be(flushStarted.Task, "flush should start when buffer threshold is hit");

        // Now try to add another line — this caller is a parallel review agent.
        // The flush timeout (200ms) should release _flushGate, allowing AddLineAsync to proceed.
        // Without the fix this blocks for ~30 seconds.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var parallelTask = Task.Run(async () =>
            await batcher.AddLineAsync("parallel-agent-output", cts.Token));

        var completedInTime = await Task.WhenAny(parallelTask, Task.Delay(TimeSpan.FromSeconds(5)));
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
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trigger = new ManualFlushTrigger();

        await using var batcher = new OutputBatcher(trigger, flushTimeout: TimeSpan.FromMilliseconds(200));
        batcher.OnFlush += async _ =>
        {
            flushStarted.TrySetResult();
            // Simulate indefinitely blocked InvokeAsync on half-open connection
            await Task.Delay(TimeSpan.FromSeconds(60));
        };

        // Fill buffer to trigger threshold flush
        for (var i = 0; i < 49; i++)
            await batcher.AddLineAsync($"line-{i}");

        // 50th line triggers the blocking flush via threshold (no timer needed)
        var triggerTask = Task.Run(async () => await batcher.AddLineAsync("trigger"));
        await Task.WhenAny(flushStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        // Simulate 3 parallel review agents trying to emit output
        // Each AddLineAsync acquires _lock (fast) — none cross the threshold alone,
        // so they don't call SendBatchAsync directly. They complete as soon as _lock is free.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var agent1 = Task.Run(async () => await batcher.AddLineAsync("agent-1", cts.Token));
        var agent2 = Task.Run(async () => await batcher.AddLineAsync("agent-2", cts.Token));
        var agent3 = Task.Run(async () => await batcher.AddLineAsync("agent-3", cts.Token));

        // All agents should complete within 5s once the flush timeout releases _flushGate.
        // AddLineAsync only blocks on _lock, not _flushGate, so these complete as soon as
        // the triggered flush's lock release propagates.
        var allAgents = Task.WhenAll(agent1, agent2, agent3);
        var completed = await Task.WhenAny(allAgents, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(allAgents,
            "all parallel review agents should unblock within a bounded time " +
            "when the flush handler is hung (flush timeout should release the lock)");
    }

    /// <summary>
    /// After a flush timeout fires, the batcher's flush loop should continue
    /// operating normally — subsequent trigger ticks should flush new batches.
    ///
    /// Uses ManualFlushTrigger to fire ticks explicitly, eliminating all real-time waits.
    /// Sequence: add line → Tick() → first flush starts (blocks) → timeout fires →
    ///           add second line → Tick() → second flush completes → assert flushCount > 1.
    /// </summary>
    [Fact]
    public async Task AfterFlushTimeout_TickBasedFlushContinuesWorking()
    {
        var flushCount = 0;
        var firstFlushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFlushCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var trigger = new ManualFlushTrigger();
        // 200ms timeout — still real-time, but only this one wait is unavoidable
        // since we're testing that the timeout actually fires.
        await using var batcher = new OutputBatcher(trigger, flushTimeout: TimeSpan.FromMilliseconds(200));

        batcher.OnFlush += async _ =>
        {
            var count = Interlocked.Increment(ref flushCount);
            if (count == 1)
            {
                firstFlushStarted.TrySetResult();
                // First flush blocks — simulates hung SignalR connection
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            else
            {
                // Subsequent flushes complete immediately and signal recovery
                secondFlushCompleted.TrySetResult();
            }
        };

        // Add first line, then tick to wake the flush loop
        await batcher.AddLineAsync("first-line");
        trigger.Tick();

        // Wait for the blocking flush handler to start — event-driven, no fixed delay
        var firstStarted = await Task.WhenAny(firstFlushStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        firstStarted.Should().Be(firstFlushStarted.Task, "first flush should start after tick");

        // The flush timeout (200ms) will fire and release _flushGate.
        // We need to wait at least that long — this is the only unavoidable real-time wait,
        // and it's bounded by the flush timeout we configured, not by scheduler jitter.
        await Task.Delay(TimeSpan.FromMilliseconds(400)); // 2× timeout for CI headroom

        // Add second line and tick — the flush loop should now process it normally
        await batcher.AddLineAsync("second-line");
        trigger.Tick();

        // Wait for the second flush to complete — event-driven, no fixed delay
        var secondFired = await Task.WhenAny(secondFlushCompleted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        secondFired.Should().Be(secondFlushCompleted.Task,
            "after a flush timeout, the tick-based flush loop should recover and " +
            "continue flushing subsequent batches normally");

        Interlocked.CompareExchange(ref flushCount, 0, 0).Should().BeGreaterThan(1,
            "flushCount should be > 1 once the second flush completes");
    }
}
