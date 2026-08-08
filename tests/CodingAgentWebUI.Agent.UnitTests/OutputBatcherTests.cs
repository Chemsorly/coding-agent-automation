using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="OutputBatcher"/>.
/// </summary>
public class OutputBatcherTests
{
    [Fact]
    public async Task FlushesAt50LinesThreshold()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        await using var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        // Act — add exactly 50 lines
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Assert — should have flushed exactly once with 50 lines
        flushedBatches.Should().HaveCount(1);
        flushedBatches[0].Should().HaveCount(50);
        flushedBatches[0][0].Should().Be("line-0");
        flushedBatches[0][49].Should().Be("line-49");
    }

    [Fact]
    public async Task FlushesAt250msTimerInterval()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        await using var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        // Act — add fewer than 50 lines (won't trigger threshold flush)
        await batcher.AddLineAsync("timer-line-1");
        await batcher.AddLineAsync("timer-line-2");

        // Poll until the timer flushes (no fixed delay)
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!flushedBatches.Any() && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Assert — timer should have flushed the 2 lines
        flushedBatches.Should().HaveCountGreaterThanOrEqualTo(1);
        var allLines = flushedBatches.SelectMany(b => b).ToList();
        allLines.Should().Contain("timer-line-1");
        allLines.Should().Contain("timer-line-2");
    }

    [Fact]
    public async Task DisposeAsyncFlushesRemainingLines()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        // Act — add lines but don't wait for timer
        await batcher.AddLineAsync("remaining-1");
        await batcher.AddLineAsync("remaining-2");
        await batcher.AddLineAsync("remaining-3");

        // Dispose immediately (before timer fires)
        await batcher.DisposeAsync();

        // Assert — remaining lines should have been flushed during disposal
        var allLines = flushedBatches.SelectMany(b => b).ToList();
        allLines.Should().Contain("remaining-1");
        allLines.Should().Contain("remaining-2");
        allLines.Should().Contain("remaining-3");
    }

    [Fact]
    public async Task EmptyBufferDoesNotTriggerFlush()
    {
        // Arrange
        var flushCount = 0;
        await using var batcher = new OutputBatcher();
        batcher.OnFlush += _ =>
        {
            Interlocked.Increment(ref flushCount);
            return Task.CompletedTask;
        };

        // Act — don't add any lines, wait long enough for multiple timer ticks
        // Poll to confirm no flush fires (negative assertion needs a reasonable wait)
        var deadline = DateTime.UtcNow.AddMilliseconds(800);
        while (DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Assert — no flush should have been triggered
        flushCount.Should().Be(0);
    }

    [Fact]
    public async Task MultipleBatchesFlushCorrectly()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        // Act — add 120 lines (should trigger 2 threshold flushes at 50 each, 20 remaining)
        for (var i = 0; i < 120; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Dispose flushes remaining lines deterministically (no timer dependency)
        await batcher.DisposeAsync();

        // Assert — should have all 120 lines across threshold + final flush
        var allLines = flushedBatches.SelectMany(b => b).ToList();
        allLines.Should().HaveCount(120);
    }
}

/// <summary>
/// Tests using <see cref="ManualFlushTrigger"/> for deterministic, real-time-free control.
/// </summary>
public class OutputBatcherManualTriggerTests
{
    [Fact]
    public async Task ManualFlushTrigger_Tick_FlushesLines()
    {
        var trigger = new ManualFlushTrigger();
        var flushedBatches = new List<IReadOnlyList<string>>();
        await using var batcher = new OutputBatcher(trigger);
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        await batcher.AddLineAsync("line-a");
        await batcher.AddLineAsync("line-b");

        // No flush yet — trigger hasn't ticked
        flushedBatches.Should().BeEmpty();

        // Tick the trigger — this should cause a flush
        trigger.Tick();

        // Give the flush loop a moment to process
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (flushedBatches.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var allLines = flushedBatches.SelectMany(b => b).ToList();
        allLines.Should().Contain("line-a");
        allLines.Should().Contain("line-b");
    }

    [Fact]
    public async Task ManualFlushTrigger_Stop_StopsFlushLoop()
    {
        var trigger = new ManualFlushTrigger();
        var flushCount = 0;
        await using var batcher = new OutputBatcher(trigger);
        batcher.OnFlush += _ =>
        {
            Interlocked.Increment(ref flushCount);
            return Task.CompletedTask;
        };

        await batcher.AddLineAsync("line-1");

        // Stop the trigger — WaitForNextTickAsync should return false
        trigger.Stop();

        // Wait briefly to confirm the loop exited cleanly
        await Task.Delay(50);

        // No tick fired, so no flush from trigger
        flushCount.Should().Be(0, "stop before tick should not flush");
    }

    [Fact]
    public async Task ManualFlushTrigger_WaitForNextTickAsync_ReturnsFalseAfterStop()
    {
        var trigger = new ManualFlushTrigger();
        trigger.Stop();

        var result = await trigger.WaitForNextTickAsync(CancellationToken.None);

        result.Should().BeFalse("WaitForNextTickAsync should return false after Stop()");
    }

    [Fact]
    public async Task OnFlushHandlerException_DoesNotCrashBatcher()
    {
        var trigger = new ManualFlushTrigger();
        var secondFlushSaw = new List<string>();
        var firstFlushAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var batcher = new OutputBatcher(trigger);

        var callCount = 0;
        batcher.OnFlush += batch =>
        {
            // Interlocked required: handler runs on flush loop thread, test reads on test thread
            var myCount = Interlocked.Increment(ref callCount);
            if (myCount == 1)
            {
                firstFlushAttempted.TrySetResult();
                throw new InvalidOperationException("Simulated flush handler failure");
            }

            secondFlushSaw.AddRange(batch);
            return Task.CompletedTask;
        };

        await batcher.AddLineAsync("first");
        trigger.Tick(); // First tick — flush throws

        // Wait for the first flush attempt to complete before ticking again —
        // event-driven avoids a fixed Task.Delay(50) that can be too short on loaded CI
        await Task.WhenAny(firstFlushAttempted.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        await batcher.AddLineAsync("second");
        trigger.Tick(); // Second tick — flush succeeds
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (secondFlushSaw.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        secondFlushSaw.Should().Contain("second", "batcher should survive an OnFlush exception");
    }

    [Fact]
    public async Task FlushTimeout_AbandonedFlush_DoesNotBlockSubsequentFlushes()
    {
        var trigger = new ManualFlushTrigger();
        var secondFlushLines = new List<string>();

        // Very short flush timeout — 20ms
        await using var batcher = new OutputBatcher(trigger, flushTimeout: TimeSpan.FromMilliseconds(20));

        var callCount = 0;
        batcher.OnFlush += async batch =>
        {
            // Interlocked.Increment is required: the abandoned first handler continues
            // running concurrently after the timeout, so it races with subsequent
            // invocations on the shared counter. A plain ++ has no memory barrier.
            var myCount = Interlocked.Increment(ref callCount);
            if (myCount == 1)
            {
                // Hang longer than the timeout
                await Task.Delay(500);
            }
            else
            {
                secondFlushLines.AddRange(batch);
            }
        };

        await batcher.AddLineAsync("timeout-line");
        trigger.Tick(); // First tick — flush will time out after 20ms

        // Wait for the timeout to abandon the first flush
        await Task.Delay(150);

        await batcher.AddLineAsync("post-timeout");
        trigger.Tick(); // Second tick — should proceed even though first was abandoned

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (secondFlushLines.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        secondFlushLines.Should().Contain("post-timeout",
            "batcher should continue delivering after a timed-out flush is abandoned");
    }

    [Fact]
    public async Task InfiniteFlushTimeout_WaitsForHandlerCompletion()
    {
        var trigger = new ManualFlushTrigger();
        var tcs = new TaskCompletionSource<bool>();
        var flushed = new List<string>();

        await using var batcher = new OutputBatcher(trigger, flushTimeout: Timeout.InfiniteTimeSpan);
        batcher.OnFlush += async batch =>
        {
            await tcs.Task; // Block until released
            flushed.AddRange(batch);
        };

        await batcher.AddLineAsync("infinite-line");
        trigger.Tick();

        // Handler is blocked — give a moment for the tick to reach SendBatchAsync
        await Task.Delay(50);
        flushed.Should().BeEmpty("handler still blocked");

        // Unblock the handler
        tcs.SetResult(true);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (flushed.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        flushed.Should().Contain("infinite-line", "handler should complete when unblocked");
    }
}
