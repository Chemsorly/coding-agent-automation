using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="OutputBatcherHubExtensions"/>.
/// </summary>
public class OutputBatcherHubExtensionsTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    [Fact]
    public async Task CreateWithHubFlush_InvokesFlushActionWithBatchedLines()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        await using var batcher = OutputBatcherHubExtensions.CreateWithHubFlush(
            lines =>
            {
                flushedBatches.Add(lines.ToList());
                return Task.CompletedTask;
            },
            _mockLogger.Object);

        // Act — add 50 lines to trigger threshold flush
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Assert
        flushedBatches.Should().HaveCount(1);
        flushedBatches[0].Should().HaveCount(50);
        flushedBatches[0][0].Should().Be("line-0");
        flushedBatches[0][49].Should().Be("line-49");
    }

    [Fact]
    // TODO: Add a variant test using `_ => Task.FromException(expectedException)` to cover the async-faulted-task
    // code path, which is what real HubConnection.InvokeAsync failures produce (vs synchronous throw tested here).
    public async Task CreateWithHubFlush_CatchesExceptionAndLogsWarning()
    {
        // Arrange
        var expectedException = new InvalidOperationException("SignalR connection lost");
        await using var batcher = OutputBatcherHubExtensions.CreateWithHubFlush(
            _ => throw expectedException,
            _mockLogger.Object);

        // Act — add 50 lines to trigger flush (which will throw)
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Assert — exception was caught and logged, batcher didn't crash
        _mockLogger.Verify(
            l => l.Warning(expectedException, "Failed to send output lines batch"),
            Times.Once);
    }

    [Fact]
    public async Task CreateWithHubFlush_UsesCustomFailureMessage()
    {
        // Arrange
        var expectedException = new TimeoutException("Hub timeout");
        await using var batcher = OutputBatcherHubExtensions.CreateWithHubFlush(
            _ => throw expectedException,
            _mockLogger.Object,
            "Failed to send chat response lines");

        // Act
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"line-{i}");

        // Assert
        _mockLogger.Verify(
            l => l.Warning(expectedException, "Failed to send chat response lines"),
            Times.Once);
    }

    [Fact]
    // TODO: This test is tautological — it asserts a compile-time type constraint (IAsyncDisposable) that
    // holds regardless of how the batcher was created. Consider replacing with a behavioral assertion.
    public async Task CreateWithHubFlush_ReturnedBatcherIsAsyncDisposable()
    {
        // Arrange & Act
        var batcher = OutputBatcherHubExtensions.CreateWithHubFlush(
            _ => Task.CompletedTask,
            _mockLogger.Object);

        // Assert — should dispose without error
        batcher.Should().BeAssignableTo<IAsyncDisposable>();
        await batcher.DisposeAsync();
    }

    [Fact]
    public async Task CreateWithHubFlush_FlushesRemainingLinesOnDispose()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        var batcher = OutputBatcherHubExtensions.CreateWithHubFlush(
            lines =>
            {
                flushedBatches.Add(lines.ToList());
                return Task.CompletedTask;
            },
            _mockLogger.Object);

        // Act — add fewer than 50 lines (won't trigger threshold flush)
        await batcher.AddLineAsync("remaining-line-1");
        await batcher.AddLineAsync("remaining-line-2");

        // Dispose should flush remaining lines
        await batcher.DisposeAsync();

        // Assert
        flushedBatches.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new[] { "remaining-line-1", "remaining-line-2" });
    }

    [Fact]
    public async Task CreateWithHubFlush_ContinuesWorkingAfterFlushException()
    {
        // Arrange
        var callCount = 0;
        var flushedBatches = new List<IReadOnlyList<string>>();
        await using var batcher = OutputBatcherHubExtensions.CreateWithHubFlush(
            lines =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("First flush fails");
                flushedBatches.Add(lines.ToList());
                return Task.CompletedTask;
            },
            _mockLogger.Object);

        // Act — first batch of 50 (will fail), then second batch of 50 (should succeed)
        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"batch1-{i}");

        for (var i = 0; i < 50; i++)
            await batcher.AddLineAsync($"batch2-{i}");

        // Assert — second batch should have been flushed successfully
        flushedBatches.Should().HaveCount(1);
        flushedBatches[0][0].Should().Be("batch2-0");
    }
}
