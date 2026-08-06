using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for <see cref="OutputBatcher"/> covering the SendBatchAsync and DisposeAsync paths.
/// These tests use <see cref="ManualFlushTrigger"/> to control flush timing without real delays.
/// Tests are placed here (Infrastructure.UnitTests) so that coverage is correctly attributed
/// to the CodingAgentWebUI.Infrastructure assembly by coverlet.
/// </summary>
public class OutputBatcherTests
{
    [Fact]
    public async Task SendBatchAsync_FlushesLinesViaOnFlush()
    {
        // Arrange — ManualFlushTrigger avoids any real-time delays
        var trigger = new ManualFlushTrigger();
        var received = new List<string>();
        await using var batcher = new OutputBatcher(trigger);
        batcher.OnFlush += lines =>
        {
            received.AddRange(lines);
            return Task.CompletedTask;
        };

        // Act — add a line and trigger one flush tick; SendBatchAsync is called via the flush loop
        await batcher.AddLineAsync("hello");
        trigger.Tick();

        // Wait for the flush loop to process the tick
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (received.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Assert
        received.Should().Contain("hello");
    }

    [Fact]
    public async Task SendBatchAsync_CalledWhenBatchSizeExceeded()
    {
        // Arrange — threshold flush (50 lines) bypasses the timer and calls SendBatchAsync directly
        // Use default constructor (real PeriodicTimer) to avoid ManualFlushTrigger disposal race
        var flushedBatches = new List<IReadOnlyList<string>>();
        var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        // Act — add exactly 50 lines to trigger threshold flush
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Assert — threshold flush fires synchronously; SendBatchAsync was invoked
        flushedBatches.Should().HaveCount(1);
        flushedBatches[0].Should().HaveCount(50);
        flushedBatches[0][0].Should().Be("line-0");
        flushedBatches[0][49].Should().Be("line-49");

        await batcher.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_FlushesRemainingLinesViaBatchAsync()
    {
        // Arrange — use ManualFlushTrigger so the flush loop doesn't consume lines before dispose
        var trigger = new ManualFlushTrigger();
        var received = new List<string>();
        var batcher = new OutputBatcher(trigger);
        batcher.OnFlush += lines =>
        {
            received.AddRange(lines);
            return Task.CompletedTask;
        };

        // Act — add lines but don't tick the trigger; DisposeAsync must flush them
        await batcher.AddLineAsync("remaining-1");
        await batcher.AddLineAsync("remaining-2");
        await batcher.DisposeAsync();

        // Assert — dispose path called _lock.WaitAsync(CancellationToken.None) and flushed remaining
        received.Should().Contain("remaining-1");
        received.Should().Contain("remaining-2");
    }

    [Fact]
    public async Task DisposeAsync_WhenBufferIsEmpty_CompletesCleanly()
    {
        // Arrange — nothing in buffer; tests the DisposeAsync fast path
        var trigger = new ManualFlushTrigger();
        var flushCount = 0;
        var batcher = new OutputBatcher(trigger);
        batcher.OnFlush += _ =>
        {
            Interlocked.Increment(ref flushCount);
            return Task.CompletedTask;
        };

        // Act
        await batcher.DisposeAsync();

        // Assert — no flush should have been triggered for empty buffer
        flushCount.Should().Be(0);
    }

    [Fact]
    public async Task SendBatchAsync_SerializesFlushesCorrectly()
    {
        // Arrange — use the default (production) constructor to avoid ManualFlushTrigger
        // disposal-race issues; 150 lines trigger 3 threshold flushes synchronously
        var allLines = new List<string>();
        var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            lock (allLines)
                allLines.AddRange(batch);
            return Task.CompletedTask;
        };

        // Act — add 150 lines, which triggers 3 threshold flushes (50 each) via SendBatchAsync
        for (var i = 0; i < 150; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Dispose — flushes any remaining lines and stops the flush loop cleanly
        await batcher.DisposeAsync();

        // Assert — all 150 lines must be present (all went through SendBatchAsync)
        allLines.Should().HaveCount(150);
    }

    [Fact]
    public async Task SendBatchAsync_WhenOnFlushIsNull_CompletesWithoutThrowing()
    {
        // Arrange — no OnFlush handler registered; tests the null guard in SendBatchAsync
        // Use default constructor (real PeriodicTimer) to avoid ManualFlushTrigger disposal race
        var batcher = new OutputBatcher();
        // (no OnFlush handler)

        // Act — threshold flush will call SendBatchAsync; should not throw
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Dispose cleanly
        await batcher.DisposeAsync();

        // Assert — no exception thrown (implicit: test reaches here)
    }
}
