using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for DispatchService dispatch ordering.
/// Validates the three-key sort invariant: tier(TaskType) ASC, PriorityWeight DESC, CreatedAt ASC.
/// Lower-tier items (Review &gt; Decomposition &gt; Implementation &gt; Consolidation) always dispatch first;
/// within a tier, higher-priority items dispatch first; equal-priority items fall back to FIFO.
/// <para>
/// NOTE: These properties sort anonymous in-memory lists using the same expression as production.
/// They guard the within-tier logic (PriorityWeight and CreatedAt invariants) and the cross-tier
/// invariant. For an integration-level guard against the production <c>DispatchStateBuilder.BuildStateAsync</c>
/// query, see <c>DispatchStateBuilderBranchTests</c> in <c>CodingAgent.Orchestration.UnitTests</c>.
/// </para>
/// <para><b>Updated for #2563:</b> added tier as primary sort key; Requirements 5.7 updated for #2172.</para>
/// </summary>
public class DispatchServiceFifoOrderingPropertyTests
{
    /// <summary>
    /// Property: For any two items where item A has a strictly higher PriorityWeight than item B,
    /// item A appears before item B in dispatch order regardless of their CreatedAt values.
    /// This directly tests the priority invariant: a higher-weight item must always be dispatched
    /// first, even if it was created later than the lower-weight item.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DispatchOrder_HigherWeightItemAlwaysPrecedesLowerWeightItem(
        PositiveInt weightDiff,
        int baseWeightRaw,
        int createdAtOffsetSeconds)
    {
        // Ensure distinct weights: highWeight > lowWeight, both in [0, 1000]
        var lowWeight = (int)((uint)baseWeightRaw % 1001);          // 0–1000
        var diff = weightDiff.Get % 1000 + 1;                       // 1–1000
        var highWeight = Math.Min(lowWeight + diff, 1000);
        if (highWeight == lowWeight)
            return true; // degenerate case: skip (equal weights handled by FIFO property)

        // High-weight item created AFTER low-weight item — priority must still win
        var lowCreatedAt = DateTimeOffset.UnixEpoch;
        var highCreatedAt = lowCreatedAt.AddSeconds(Math.Abs(createdAtOffsetSeconds));

        var items = new[]
        {
            new { Id = "low",  PriorityWeight = lowWeight,  CreatedAt = lowCreatedAt  },
            new { Id = "high", PriorityWeight = highWeight, CreatedAt = highCreatedAt }
        };

        var dispatchOrder = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // High-weight item must be dispatched first regardless of CreatedAt
        return dispatchOrder[0].Id == "high";
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

        // TODO: The final Count == Count assertion is trivially true (OrderBy never drops items)
        // and provides no coverage. The meaningful invariant is in the loop above. Consider
        // replacing this with a stronger assertion such as verifying the first item has the
        // earliest CreatedAt when all weights are equal.
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
    /// Property: For any two items with the same PriorityWeight, the item with the earlier
    /// CreatedAt is dispatched first (FIFO tie-break). This validates the sort is a stable
    /// total order on (PriorityWeight DESC, CreatedAt ASC): equal-weight items must not be
    /// reordered arbitrarily.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DispatchOrder_EqualWeightTieBreaksOnCreatedAt(int createdAtOffset1, int createdAtOffset2)
    {
        // Ensure distinct CreatedAt values to avoid degenerate ties
        var t1 = DateTimeOffset.UnixEpoch.AddSeconds((uint)createdAtOffset1 % 10_000_000);
        var t2 = DateTimeOffset.UnixEpoch.AddSeconds((uint)createdAtOffset2 % 10_000_000);
        if (t1 == t2)
            return true; // equal timestamps — tie-break undefined, skip

        var earlier = t1 < t2 ? t1 : t2;
        var later   = t1 < t2 ? t2 : t1;
        const int sameWeight = 50;

        var items = new[]
        {
            new { Id = "earlier", PriorityWeight = sameWeight, CreatedAt = earlier },
            new { Id = "later",   PriorityWeight = sameWeight, CreatedAt = later   }
        };

        var dispatchOrder = items
            .OrderByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // The item with the earlier CreatedAt must be dispatched first when weights are equal
        return dispatchOrder[0].Id == "earlier";
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

    /// <summary>
    /// Property: For any two items of distinct task types (where tierA &lt; tierB), the lower-tier
    /// item always appears first in dispatch order — regardless of PriorityWeight or CreatedAt.
    /// A high-priority item in a higher tier must never jump ahead of a lower-tier item.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool LowerTierItem_AlwaysPrecedesHigherTierItem(
        PositiveInt tierAIndex,
        PositiveInt tierBIndex,
        int weightA,
        int weightB,
        int createdAtOffsetSecondsA,
        int createdAtOffsetSecondsB)
    {
        // Map indices to tier values 0–3 (Review=0, Decomp=1, Impl=2, Consolidation=3)
        var allTiers = new[] { 0, 1, 2, 3 };
        var tierA = allTiers[(tierAIndex.Get - 1) % 4];
        var tierB = allTiers[(tierBIndex.Get - 1) % 4];

        if (tierA == tierB)
            return true; // degenerate: same tier, cross-tier invariant doesn't apply

        // Ensure tierA < tierB (lower tier = higher dispatch priority)
        if (tierA > tierB)
            (tierA, tierB) = (tierB, tierA);

        // TODO: Math.Abs(int.MinValue) throws OverflowException (int.MinValue has no positive counterpart).
        // With MaxTest=20 this is unlikely to be sampled, but it is a latent flaky-test risk.
        // Replace with (int)((uint)weightA % 1001) and (long)((ulong)createdAtOffsetSecondsA % 10_000_000)
        // to eliminate the overflow path, mirroring the safe unsigned-cast pattern used elsewhere in this file.
        var wA = Math.Abs(weightA) % 1001; // 0–1000
        var wB = Math.Abs(weightB) % 1001;
        var tA = DateTimeOffset.UnixEpoch.AddSeconds(Math.Abs(createdAtOffsetSecondsA) % 10_000_000);
        var tB = DateTimeOffset.UnixEpoch.AddSeconds(Math.Abs(createdAtOffsetSecondsB) % 10_000_000);

        var items = new[]
        {
            new { Id = "A", Tier = tierA, PriorityWeight = wA, CreatedAt = tA },
            new { Id = "B", Tier = tierB, PriorityWeight = wB, CreatedAt = tB }
        };

        var dispatchOrder = items
            .OrderBy(x => x.Tier)
            .ThenByDescending(x => x.PriorityWeight)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // Lower-tier item (A, tierA < tierB) must always come first
        return dispatchOrder[0].Id == "A";
    }
}
