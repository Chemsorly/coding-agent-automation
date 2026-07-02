// Feature: 035a-postgres-work-queue
// Property 1: Work Item State Machine Validity
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based test asserting that WorkItemTransitionService.IsValidTransition
/// returns true if and only if the (current, target) pair is in the allowed transition set.
/// **Validates: Requirements 7.5**
/// </summary>
public class WorkItemStateMachinePropertyTests
{
    /// <summary>
    /// The exhaustive set of allowed state transitions per the work item state machine:
    /// - Pending → Dispatched, Failed, Cancelled
    /// - Dispatched → Running, Failed, Cancelled, Pending (re-queue on rejection)
    /// - Running → Succeeded, Failed, Cancelled
    /// All other pairs (including self-transitions) must be rejected.
    /// </summary>
    private static readonly HashSet<(WorkItemStatus Current, WorkItemStatus Target)> AllowedTransitions =
    [
        (WorkItemStatus.Pending, WorkItemStatus.Dispatched),
        (WorkItemStatus.Pending, WorkItemStatus.Failed),
        (WorkItemStatus.Pending, WorkItemStatus.Cancelled),
        (WorkItemStatus.Dispatched, WorkItemStatus.Running),
        (WorkItemStatus.Dispatched, WorkItemStatus.Failed),
        (WorkItemStatus.Dispatched, WorkItemStatus.Cancelled),
        (WorkItemStatus.Dispatched, WorkItemStatus.Pending),
        (WorkItemStatus.Running, WorkItemStatus.Succeeded),
        (WorkItemStatus.Running, WorkItemStatus.Failed),
        (WorkItemStatus.Running, WorkItemStatus.Cancelled),
    ];

    /// <summary>
    /// Property 1: Work Item State Machine Validity
    /// For any generated (currentStatus, targetStatus) pair, IsValidTransition returns true
    /// if and only if the pair is in the allowed transition set.
    /// **Validates: Requirements 7.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(WorkItemStatusPairArbitraries) })]
    public void IsValidTransition_ReturnsTrue_IffPairInAllowedSet(WorkItemStatus current, WorkItemStatus target)
    {
        var expected = AllowedTransitions.Contains((current, target));
        var actual = WorkItemTransitionService.IsValidTransition(current, target);

        if (actual != expected)
        {
            throw new Exception(
                $"IsValidTransition({current}, {target}) returned {actual} but expected {expected}. " +
                $"Pair is {(expected ? "in" : "NOT in")} allowed set.");
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for WorkItemStatus pairs.
/// Generates all possible combinations of (WorkItemStatus, WorkItemStatus).
/// </summary>
public class WorkItemStatusPairArbitraries
{
    public static Arbitrary<WorkItemStatus> WorkItemStatusArb()
    {
        var gen = Gen.Elements(
            WorkItemStatus.Pending,
            WorkItemStatus.Dispatched,
            WorkItemStatus.Running,
            WorkItemStatus.Succeeded,
            WorkItemStatus.Failed,
            WorkItemStatus.Cancelled);
        return gen.ToArbitrary();
    }
}

/// <summary>
/// Compile-time guard: asserts that WorkItemStatus terminal-state ordinals match
/// the partial unique index filter in PipelineDbContext's OnModelCreating.
/// If this test fails, the migration filter "Status" NOT IN (3, 4, 5) is stale
/// and must be regenerated.
/// </summary>
public class WorkItemStatusOrdinalGuardTests
{
    [Fact]
    public void TerminalStatusOrdinals_MatchMigrationFilter()
    {
        // The partial unique index in PipelineDbContext uses:
        //   .HasFilter("\"Status\" NOT IN (3, 4, 5)")
        // These MUST correspond to Succeeded=3, Failed=4, Cancelled=5.
        Assert.Equal(3, (int)WorkItemStatus.Succeeded);
        Assert.Equal(4, (int)WorkItemStatus.Failed);
        Assert.Equal(5, (int)WorkItemStatus.Cancelled);
    }

    [Fact]
    public void NonTerminalStatusOrdinals_AreBelow3()
    {
        // Sanity: non-terminal statuses must have ordinals < 3
        // to NOT be excluded by the partial unique index.
        Assert.Equal(0, (int)WorkItemStatus.Pending);
        Assert.Equal(1, (int)WorkItemStatus.Dispatched);
        Assert.Equal(2, (int)WorkItemStatus.Running);
    }
}
