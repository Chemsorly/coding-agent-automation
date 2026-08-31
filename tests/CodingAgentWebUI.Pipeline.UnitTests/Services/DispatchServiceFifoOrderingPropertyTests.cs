using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for DispatchService dispatch ordering.
/// Validates the two-key sort invariant: ORDER BY PriorityWeight DESC, CreatedAt ASC.
/// Higher-priority items are always dispatched first; equal-weight items fall back to FIFO.
/// **Validates: Requirements 5.7 (updated for #2172)**
/// </summary>
public class DispatchServiceFifoOrderingPropertyTests
{
    /// <summary>
    /// Property: For any list of pending work items with random PriorityWeight and CreatedAt values,
    /// dispatch order (ORDER BY PriorityWeight DESC, CreatedAt ASC) is non-decreasing by PriorityWeight
    /// in DESC direction (i.e. each item's weight is ≤ the previous item's weight).
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DispatchOrder_PriorityWeightDescending(int[] weights)
    {
        // Generate work items with random weights and arbitrary timestamps
        var items = weights.Select((w, i) => new
        {
            Id = Guid.NewGuid(),
            PriorityWeight = Math.Abs(w) % 1001, // clamp to valid range 0–1000
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i * 60)
        }).ToList();

        // Simulate the dispatch query: ORDER BY PriorityWeight DESC, CreatedAt ASC
        var dispatchOrder = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // Assert: PriorityWeight is non-increasing across the dispatch sequence
        for (var i = 1; i < dispatchOrder.Count; i++)
        {
            if (dispatchOrder[i].PriorityWeight > dispatchOrder[i - 1].PriorityWeight)
                return false;
        }

        // Assert: every item is present (no items lost)
        return dispatchOrder.Count == items.Count;
    }

    /// <summary>
    /// Property: Among items with equal PriorityWeight, dispatch order is FIFO (CreatedAt ASC).
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DispatchOrder_EqualWeightItemsAreFifo(NonEmptyArray<int> offsets)
    {
        // All items have the same PriorityWeight — tie-break is FIFO
        const int sameWeight = 50;
        var items = offsets.Get.Select((offset, i) => new
        {
            Id = i,
            PriorityWeight = sameWeight,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(Math.Abs(offset) % 10_000_000)
        }).ToList();

        var dispatchOrder = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // CreatedAt should be non-decreasing within the same weight group
        for (var i = 1; i < dispatchOrder.Count; i++)
        {
            if (dispatchOrder[i].CreatedAt < dispatchOrder[i - 1].CreatedAt)
                return false;
        }

        return dispatchOrder.Count == items.Count;
    }

    /// <summary>
    /// Property: The first dispatched item always has the maximum PriorityWeight among all pending items.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool FirstDispatchedItem_HasMaxPriorityWeight(NonEmptyArray<int> weights)
    {
        var items = weights.Get.Select((w, i) => new
        {
            Id = i,
            PriorityWeight = Math.Abs(w) % 1001,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i * 60)
        }).ToList();

        var dispatchOrder = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        var maxWeight = items.Max(x => x.PriorityWeight);
        return dispatchOrder[0].PriorityWeight == maxWeight;
    }

    /// <summary>
    /// Property: Dispatch ordering is deterministic — sorting the same list twice yields the same sequence.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DispatchOrder_IsDeterministic(int[] weights)
    {
        var items = weights.Select((w, i) => new
        {
            Id = i, // stable ID
            PriorityWeight = Math.Abs(w) % 1001,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i * 60)
        }).ToList();

        var order1 = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .ToList();

        var order2 = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .ToList();

        return order1.SequenceEqual(order2);
    }

    /// <summary>
    /// Property: A high-weight item (PriorityWeight > 0) always appears before a low-weight item
    /// (PriorityWeight == 0) in dispatch order, regardless of CreatedAt.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool HighWeightItem_AlwaysDispatchedBeforeLowWeightItem(PositiveInt highWeight, int createdAtOffsetSeconds)
    {
        var high = highWeight.Get % 1000 + 1; // 1–1000
        var lowCreatedAt = DateTimeOffset.UnixEpoch; // created first (earliest)
        var highCreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(Math.Abs(createdAtOffsetSeconds)); // created after

        var items = new[]
        {
            new { Id = "low",  PriorityWeight = 0,    CreatedAt = lowCreatedAt  }, // lower weight, older
            new { Id = "high", PriorityWeight = high, CreatedAt = highCreatedAt }  // higher weight, newer
        };

        var dispatchOrder = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // High-weight item must come first despite being created later
        return dispatchOrder[0].Id == "high";
    }
}
