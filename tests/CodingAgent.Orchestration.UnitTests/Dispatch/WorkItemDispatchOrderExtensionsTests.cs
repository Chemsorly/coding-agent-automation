using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="WorkItemDispatchOrderExtensions.ApplyDispatchOrder"/> — the canonical
/// dispatch sort used by GET /api/work-items/pending, which WorkItemDispatchLoop polls.
/// Sort keys: RunType tier ASC (Triage &gt; Review &gt; Decomposition &gt; Implementation &gt; Consolidation),
/// then PriorityWeight DESC, then CreatedAt ASC.
/// <para>
/// These run the expression as LINQ-to-Objects, so they guard the sort keys, not the SQL
/// <c>CASE WHEN</c> translation used against PostgreSQL.
/// </para>
/// </summary>
[Trait("Feature", "DispatchOrder")]
public class WorkItemDispatchOrderExtensionsTests
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// Mixed task types are ordered by tier regardless of CreatedAt.
    /// All items have equal PriorityWeight so tier is the sole differentiator.
    /// </summary>
    [Fact]
    public void ApplyDispatchOrder_MixedTaskTypes_OrdersByTierRegardlessOfCreatedAt()
    {
        // Seed newest → oldest so that CreatedAt alone would produce the wrong order.
        var triage = Item(WorkItemTaskType.Triage, createdAt: BaseTime.AddMinutes(4));
        var review = Item(WorkItemTaskType.Review, createdAt: BaseTime.AddMinutes(3));
        var decomp = Item(WorkItemTaskType.Decomposition, createdAt: BaseTime.AddMinutes(2));
        var impl = Item(WorkItemTaskType.Implementation, createdAt: BaseTime.AddMinutes(1));
        var cons = Item(WorkItemTaskType.Consolidation, createdAt: BaseTime);

        var ids = Order(cons, impl, decomp, review, triage);

        ids.Should().Equal(new[] { triage.Id, review.Id, decomp.Id, impl.Id, cons.Id },
            "tier ordering must place Triage→Review→Decomp→Impl→Consolidation regardless of CreatedAt");
    }

    /// <summary>
    /// Within a tier, PriorityWeight DESC is the secondary sort key.
    /// A manual (weight=100) Implementation item dispatches before an automatic (weight=0) one.
    /// </summary>
    [Fact]
    public void ApplyDispatchOrder_WithinTier_HigherPriorityWeightFirst()
    {
        // low is older — without PriorityWeight ordering it would come first (FIFO).
        var low = Item(WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: BaseTime.AddMinutes(-10));
        var high = Item(WorkItemTaskType.Implementation, priorityWeight: 100, createdAt: BaseTime);

        var ids = Order(low, high);

        ids.Should().Equal(new[] { high.Id, low.Id },
            "higher PriorityWeight (100) must come before lower weight (0) within the same tier");
    }

    /// <summary>
    /// Within a tier, CreatedAt ASC (FIFO) is the tiebreaker when PriorityWeight is equal.
    /// </summary>
    [Fact]
    public void ApplyDispatchOrder_WithinTier_OlderItemFirst_WhenSamePriority()
    {
        var newer = Item(WorkItemTaskType.Implementation, createdAt: BaseTime);
        var older = Item(WorkItemTaskType.Implementation, createdAt: BaseTime.AddMinutes(-10));

        var ids = Order(newer, older);

        ids.Should().Equal(new[] { older.Id, newer.Id },
            "older item (earlier CreatedAt) must come first when PriorityWeight and tier are equal");
    }

    /// <summary>
    /// Consolidation (tier 4) is last even if it was created first (oldest CreatedAt).
    /// </summary>
    [Fact]
    public void ApplyDispatchOrder_ConsolidationIsLast_EvenIfCreatedFirst()
    {
        var cons = Item(WorkItemTaskType.Consolidation, createdAt: BaseTime.AddMinutes(-100));
        var review = Item(WorkItemTaskType.Review, createdAt: BaseTime);

        var ids = Order(cons, review);

        ids.Should().Equal(new[] { review.Id, cons.Id },
            "Review (tier 1) must come before Consolidation (tier 4) regardless of CreatedAt");
    }

    /// <summary>
    /// A manual (PriorityWeight=100) Implementation item must NOT cross the tier boundary
    /// and jump ahead of a Review item (PriorityWeight=0). Tier is the primary key.
    /// </summary>
    [Fact]
    public void ApplyDispatchOrder_ManualItemWithinTier_DoesNotCrossOtherTier()
    {
        var implManual = Item(WorkItemTaskType.Implementation, priorityWeight: 100, createdAt: BaseTime);
        var reviewAuto = Item(WorkItemTaskType.Review, priorityWeight: 0, createdAt: BaseTime.AddMinutes(10));

        var ids = Order(implManual, reviewAuto);

        ids.Should().Equal(new[] { reviewAuto.Id, implManual.Id },
            "Review (tier 1) must come before a manual Implementation (tier 3) — tier beats PriorityWeight");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static List<Guid> Order(params WorkItemEntity[] items) =>
        items.AsQueryable().ApplyDispatchOrder().Select(w => w.Id).ToList();

    private static WorkItemEntity Item(
        WorkItemTaskType taskType,
        int priorityWeight = 0,
        DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = WorkItemStatus.Pending,
        AgentSelector = "kiro,dotnet",
        TaskType = taskType,
        PriorityWeight = priorityWeight,
        CreatedAt = createdAt ?? BaseTime
    };
}
