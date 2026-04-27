using AwesomeAssertions;
using CodingAgentWebUI.Agent;

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

        // Wait for the 250ms timer to fire (give some margin)
        await Task.Delay(500);

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

        // Act — don't add any lines, wait for a couple of timer ticks
        await Task.Delay(600);

        // Assert — no flush should have been triggered
        flushCount.Should().Be(0);
    }

    [Fact]
    public async Task MultipleBatchesFlushCorrectly()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        await using var batcher = new OutputBatcher();
        batcher.OnFlush += batch =>
        {
            flushedBatches.Add(batch.ToList());
            return Task.CompletedTask;
        };

        // Act — add 120 lines (should trigger 2 threshold flushes at 50 each, 20 remaining)
        for (var i = 0; i < 120; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Wait for timer to flush remaining
        await Task.Delay(500);

        // Assert — should have at least 2 threshold flushes + 1 timer flush
        var allLines = flushedBatches.SelectMany(b => b).ToList();
        allLines.Should().HaveCount(120);
    }
}
