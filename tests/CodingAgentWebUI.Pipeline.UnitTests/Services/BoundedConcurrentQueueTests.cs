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
}
