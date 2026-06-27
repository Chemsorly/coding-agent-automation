using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Locking;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Locking;

public class InProcessDistributedLockProviderTests
{
    private readonly InProcessDistributedLockProvider _provider = new();

    [Fact]
    public async Task AcquireAsync_SerializesConcurrentCallers()
    {
        // Arrange: two tasks competing for the same lock
        var lockName = "test-serialize";
        var executionOrder = new List<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Task 1 acquires the lock first
        var task1 = Task.Run(async () =>
        {
            await using var handle = await _provider.AcquireAsync(lockName);
            executionOrder.Add(1);
            gate.SetResult(); // signal that lock is held
            await Task.Delay(100); // hold lock for 100ms
            executionOrder.Add(2);
        });

        // Task 2 waits until Task 1 holds the lock, then tries to acquire
        var task2 = Task.Run(async () =>
        {
            await gate.Task; // ensure task1 has the lock
            await using var handle = await _provider.AcquireAsync(lockName);
            executionOrder.Add(3);
        });

        // Act
        await Task.WhenAll(task1, task2);

        // Assert: task2 must wait for task1 to fully complete
        executionOrder.Should().ContainInConsecutiveOrder(1, 2, 3);
    }

    [Fact]
    public async Task DisposeReleasesLock_NextCallerCanAcquire()
    {
        var lockName = "test-release";

        // First acquire and release
        var handle = await _provider.AcquireAsync(lockName);
        await handle.DisposeAsync();

        // Second acquire should succeed immediately (not block)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var handle2 = await _provider.AcquireAsync(lockName, cts.Token);
        await handle2.DisposeAsync();
        // If we reach here without timeout, the lock was released properly
    }

    [Fact]
    public async Task DifferentLockNames_DoNotBlock()
    {
        // Arrange
        var lockA = "lock-a";
        var lockB = "lock-b";
        var bothAcquired = false;

        // Act: acquire two different locks concurrently
        await using var handleA = await _provider.AcquireAsync(lockA);
        await using var handleB = await _provider.AcquireAsync(lockB);
        bothAcquired = true;

        // Assert
        bothAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_RespectsCancellation()
    {
        var lockName = "test-cancel";

        // Hold the lock
        await using var handle = await _provider.AcquireAsync(lockName);

        // Try to acquire with immediate cancellation
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _provider.AcquireAsync(lockName, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DoubleDispose_DoesNotThrow()
    {
        var lockName = "test-double-dispose";
        var handle = await _provider.AcquireAsync(lockName);

        await handle.DisposeAsync();
        await handle.DisposeAsync(); // should not throw or double-release
    }

    [Fact]
    public async Task AcquireAsync_ThrowsOnNullOrWhitespaceLockName()
    {
        var actNull = () => _provider.AcquireAsync(null!);
        await actNull.Should().ThrowAsync<ArgumentException>();

        var actEmpty = () => _provider.AcquireAsync("");
        await actEmpty.Should().ThrowAsync<ArgumentException>();

        var actWhitespace = () => _provider.AcquireAsync("   ");
        await actWhitespace.Should().ThrowAsync<ArgumentException>();
    }
}
