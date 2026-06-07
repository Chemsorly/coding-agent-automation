using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for OutputRingBuffer capacity invariant and ordering.
/// </summary>
public class OutputRingBufferPropertyTests
{
    /// <summary>
    /// Property 12: Output Ring Buffer Capacity Invariant
    /// For any sequence of output lines, stored count never exceeds capacity.
    /// **Validates: Requirements 5.9**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Count_NeverExceedsCapacity(PositiveInt capacity, NonEmptyString[] lines)
    {
        var cap = Math.Min(capacity.Get, 1000); // Keep capacity reasonable for tests
        var buffer = new OutputRingBuffer(cap);

        foreach (var line in lines)
        {
            buffer.Add(line.Get);
            buffer.Count.Should().BeLessThanOrEqualTo(cap);
        }
    }

    /// <summary>
    /// Property 12 (continued): When full, adding discards oldest.
    /// **Validates: Requirements 5.9**
    /// </summary>
    [Property(MaxTest = 20)]
    public void WhenFull_AddingDiscardsOldest(PositiveInt extraCount)
    {
        const int capacity = 5;
        var buffer = new OutputRingBuffer(capacity);

        // Fill the buffer
        for (var i = 0; i < capacity; i++)
            buffer.Add($"line-{i}");

        // Add extra lines beyond capacity
        var extra = Math.Min(extraCount.Get, 50);
        for (var i = 0; i < extra; i++)
            buffer.Add($"extra-{i}");

        buffer.Count.Should().Be(capacity);

        var all = buffer.GetAll();
        all.Should().HaveCount(capacity);

        // The oldest retained line should be the first one that wasn't discarded
        if (extra >= capacity)
        {
            // All original lines were discarded
            all[0].Should().StartWith("extra-");
        }
        else
        {
            // Some original lines remain
            all[0].Should().Be($"line-{extra}");
        }
    }

    /// <summary>
    /// Property 12 (continued): GetAll() returns lines in insertion order (oldest to newest).
    /// **Validates: Requirements 5.9**
    /// </summary>
    [Property(MaxTest = 20)]
    public void GetAll_ReturnsLinesInInsertionOrder(PositiveInt capacity)
    {
        var cap = Math.Clamp(capacity.Get, 1, 100);
        var buffer = new OutputRingBuffer(cap);
        var totalLines = cap * 2; // Add more than capacity to test wrap-around

        for (var i = 0; i < totalLines; i++)
            buffer.Add($"{i}");

        var all = buffer.GetAll();
        all.Should().HaveCount(cap);

        // Lines should be in ascending order (oldest to newest among retained)
        for (var i = 0; i < all.Count - 1; i++)
        {
            int.Parse(all[i]).Should().BeLessThan(int.Parse(all[i + 1]));
        }

        // The newest line should be the last one added
        all[^1].Should().Be($"{totalLines - 1}");
    }

    /// <summary>
    /// Property 12 (continued): AddRange respects capacity invariant.
    /// **Validates: Requirements 5.9**
    /// </summary>
    [Property(MaxTest = 20)]
    public void AddRange_RespectsCapacityInvariant(PositiveInt capacity, NonEmptyString[] lines)
    {
        var cap = Math.Min(capacity.Get, 500);
        var buffer = new OutputRingBuffer(cap);
        var lineList = lines.Select(l => l.Get).ToList();

        buffer.AddRange(lineList);

        buffer.Count.Should().BeLessThanOrEqualTo(cap);
        buffer.GetAll().Should().HaveCountLessThanOrEqualTo(cap);
    }
}
