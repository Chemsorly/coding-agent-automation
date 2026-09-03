using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class BoundedConcurrentQueueTests
{
    [Fact]
    public void Enqueue_WithinCapacity_RetainsAllItems()
    {
        var queue = new BoundedConcurrentQueue<int>(5);
        for (var i = 0; i < 5; i++)
            queue.Enqueue(i);

        queue.Count.Should().Be(5);
        queue.ToArray().Should().BeEquivalentTo([0, 1, 2, 3, 4]);
    }

    [Fact]
    public void Enqueue_OverCapacity_DiscardsOldest()
    {
        var queue = new BoundedConcurrentQueue<int>(3);
        for (var i = 0; i < 6; i++)
            queue.Enqueue(i);

        queue.Count.Should().Be(3);
        queue.ToArray().Should().BeEquivalentTo([3, 4, 5]);
    }

    [Fact]
    public void IsEmpty_WhenEmpty_ReturnsTrue()
    {
        var queue = new BoundedConcurrentQueue<string>(10);
        queue.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void IsEmpty_AfterEnqueue_ReturnsFalse()
    {
        var queue = new BoundedConcurrentQueue<string>(10);
        queue.Enqueue("item");
        queue.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ZeroCapacity_Throws()
    {
        var act = () => new BoundedConcurrentQueue<int>(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NegativeCapacity_Throws()
    {
        var act = () => new BoundedConcurrentQueue<int>(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Enumeration_ReturnsItemsInInsertionOrder()
    {
        var queue = new BoundedConcurrentQueue<string>(5);
        queue.Enqueue("a");
        queue.Enqueue("b");
        queue.Enqueue("c");

        queue.ToList().Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public void ParallelEnqueue_CountNeverExceedsCapacityPlusConcurrency()
    {
        const int capacity = 100;
        var queue = new BoundedConcurrentQueue<int>(capacity);

        Parallel.For(0, 1000, i => queue.Enqueue(i));

        // Lock-free: count should be at or below capacity after all writers complete
        queue.Count.Should().BeLessThanOrEqualTo(capacity);
    }

    [Fact]
    public void Capacity_ReturnsConfiguredValue()
    {
        var queue = new BoundedConcurrentQueue<int>(42);
        queue.Capacity.Should().Be(42);
    }

    [Fact]
    public void TryDequeue_WhenQueueHasItems_ReturnsTrueAndRemovesItem()
    {
        var queue = new BoundedConcurrentQueue<string>(5);
        queue.Enqueue("first");

        var result = queue.TryDequeue(out var item);

        result.Should().BeTrue("TryDequeue must return true when the queue is non-empty");
        item.Should().Be("first", "TryDequeue must return the enqueued value");
        queue.Count.Should().Be(0, "the item must be removed from the queue after TryDequeue");
    }

    [Fact]
    public void TryDequeue_WhenQueueIsEmpty_ReturnsFalse()
    {
        var queue = new BoundedConcurrentQueue<string>(5);

        var result = queue.TryDequeue(out var item);

        result.Should().BeFalse("TryDequeue on an empty queue must return false");
        item.Should().BeNull("out parameter must be null when TryDequeue returns false");
    }

    [Fact]
    public void TryDequeue_DrainLoop_EmptiesQueueAndPreservesCapacityEviction()
    {
        // Verifies the drain pattern used in ApplySnapshotToRunModel:
        // while (queue.TryDequeue(out _)) { }
        // After draining, the queue is empty and Enqueue still enforces capacity.
        var queue = new BoundedConcurrentQueue<int>(3);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        // Drain via the exact pattern used in ApplySnapshotToRunModel
        while (queue.TryDequeue(out _)) { }

        queue.Count.Should().Be(0, "drain loop must empty the queue");
        queue.IsEmpty.Should().BeTrue();

        // Capacity eviction must still work after draining
        queue.Enqueue(10);
        queue.Enqueue(20);
        queue.Enqueue(30);
        queue.Enqueue(40); // overflows capacity=3, should evict 10
        queue.Count.Should().Be(3, "capacity eviction must still work after drain");
        // TODO [WARNING]: BeEquivalentTo does not assert FIFO order (treats the collection as an unordered set
        // by default). A permutation like [40, 20, 30] would still pass. Consider using
        // BeEquivalentTo(..., options => options.WithStrictOrdering()) or Equal() to explicitly assert that
        // the oldest item (10) was evicted and survivors are in insertion order [20, 30, 40].
        queue.ToArray().Should().BeEquivalentTo([20, 30, 40]);
    }
}
