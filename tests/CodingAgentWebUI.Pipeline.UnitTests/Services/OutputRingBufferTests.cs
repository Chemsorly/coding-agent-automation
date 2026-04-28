using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="OutputRingBuffer"/>.
/// </summary>
public class OutputRingBufferTests
{
    [Fact]
    public void Constructor_DefaultCapacity_Is10000()
    {
        var buffer = new OutputRingBuffer();
        buffer.Capacity.Should().Be(10_000);
    }

    [Fact]
    public void Constructor_CustomCapacity_IsRespected()
    {
        var buffer = new OutputRingBuffer(50);
        buffer.Capacity.Should().Be(50);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_InvalidCapacity_Throws(int capacity)
    {
        var act = () => new OutputRingBuffer(capacity);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Count_EmptyBuffer_ReturnsZero()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public void Add_SingleLine_IncrementsCount()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.Add("hello");
        buffer.Count.Should().Be(1);
    }

    [Fact]
    public void Add_MultipleLines_TracksCount()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.Add("line1");
        buffer.Add("line2");
        buffer.Add("line3");
        buffer.Count.Should().Be(3);
    }

    [Fact]
    public void GetAll_ReturnsLinesInInsertionOrder()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.Add("first");
        buffer.Add("second");
        buffer.Add("third");

        var result = buffer.GetAll();

        result.Should().HaveCount(3);
        result[0].Should().Be("first");
        result[1].Should().Be("second");
        result[2].Should().Be("third");
    }

    [Fact]
    public void GetAll_EmptyBuffer_ReturnsEmptyList()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Add_ExceedsCapacity_DiscardsOldestLines()
    {
        var buffer = new OutputRingBuffer(3);
        buffer.Add("a");
        buffer.Add("b");
        buffer.Add("c");
        buffer.Add("d"); // should discard "a"

        buffer.Count.Should().Be(3);
        var result = buffer.GetAll();
        result[0].Should().Be("b");
        result[1].Should().Be("c");
        result[2].Should().Be("d");
    }

    [Fact]
    public void Add_WrapsAroundMultipleTimes()
    {
        var buffer = new OutputRingBuffer(3);
        for (var i = 0; i < 10; i++)
            buffer.Add($"line-{i}");

        buffer.Count.Should().Be(3);
        var result = buffer.GetAll();
        result[0].Should().Be("line-7");
        result[1].Should().Be("line-8");
        result[2].Should().Be("line-9");
    }

    [Fact]
    public void AddRange_AddsMultipleLines()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.AddRange(new[] { "a", "b", "c" });

        buffer.Count.Should().Be(3);
        var result = buffer.GetAll();
        result[0].Should().Be("a");
        result[1].Should().Be("b");
        result[2].Should().Be("c");
    }

    [Fact]
    public void AddRange_ExceedsCapacity_DiscardsOldest()
    {
        var buffer = new OutputRingBuffer(3);
        buffer.Add("existing");
        buffer.AddRange(new[] { "x", "y", "z" });

        buffer.Count.Should().Be(3);
        var result = buffer.GetAll();
        result[0].Should().Be("x");
        result[1].Should().Be("y");
        result[2].Should().Be("z");
    }

    [Fact]
    public void AddRange_LargerThanCapacity_KeepsLastN()
    {
        var buffer = new OutputRingBuffer(3);
        buffer.AddRange(new[] { "1", "2", "3", "4", "5" });

        buffer.Count.Should().Be(3);
        var result = buffer.GetAll();
        result[0].Should().Be("3");
        result[1].Should().Be("4");
        result[2].Should().Be("5");
    }

    [Fact]
    public void AddRange_NullInput_Throws()
    {
        var buffer = new OutputRingBuffer(10);
        var act = () => buffer.AddRange(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddRange_EmptyList_DoesNothing()
    {
        var buffer = new OutputRingBuffer(10);
        buffer.Add("existing");
        buffer.AddRange(Array.Empty<string>());

        buffer.Count.Should().Be(1);
        buffer.GetAll()[0].Should().Be("existing");
    }

    [Fact]
    public void Capacity1_Buffer_WorksCorrectly()
    {
        var buffer = new OutputRingBuffer(1);
        buffer.Add("first");
        buffer.Add("second");

        buffer.Count.Should().Be(1);
        buffer.GetAll()[0].Should().Be("second");
    }

    [Fact]
    public void ThreadSafety_ConcurrentAdds_DoNotCorrupt()
    {
        var buffer = new OutputRingBuffer(1000);
        var tasks = Enumerable.Range(0, 10)
            .Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                    buffer.Add($"thread-{t}-line-{i}");
            }))
            .ToArray();

        Task.WaitAll(tasks);

        // All 1000 lines should be present (capacity = 1000, total adds = 1000)
        buffer.Count.Should().Be(1000);
    }

    [Fact]
    public void ThreadSafety_ConcurrentAddsExceedingCapacity_MaintainsInvariant()
    {
        var buffer = new OutputRingBuffer(100);
        var tasks = Enumerable.Range(0, 10)
            .Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                    buffer.Add($"thread-{t}-line-{i}");
            }))
            .ToArray();

        Task.WaitAll(tasks);

        // Count should never exceed capacity
        buffer.Count.Should().BeLessThanOrEqualTo(100);
        buffer.GetAll().Should().HaveCount(buffer.Count);
    }
}
