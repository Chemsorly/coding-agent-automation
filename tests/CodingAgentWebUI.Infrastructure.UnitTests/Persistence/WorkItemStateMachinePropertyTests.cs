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
/// Property: Any random sequence of valid transitions starting from Pending
/// never reaches an undefined state and always terminates at a valid WorkItemStatus.
/// This verifies the state machine is well-formed: no transition sequence can
/// put a work item into an inconsistent lifecycle position.
/// </summary>
public class WorkItemStateMachineReachabilityPropertyTests
{
    private static readonly WorkItemStatus[] AllStatuses = Enum.GetValues<WorkItemStatus>();
    private static readonly WorkItemStatus[] TerminalStatuses =
        [WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled];

    /// <summary>
    /// Property: Random walk from Pending always ends at a terminal state or stays
    /// in a non-terminal state that has valid outgoing transitions.
    /// No valid transition sequence can "strand" a work item in a state with no exit.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool RandomWalk_FromPending_NeverStrands(int seed)
    {
        var rng = new Random(seed);
        var current = WorkItemStatus.Pending;
        var steps = 0;
        const int maxSteps = 100;

        while (steps < maxSteps)
        {
            // If terminal, stop — work item lifecycle is complete
            if (TerminalStatuses.Contains(current))
                return true;

            // Find all valid outgoing transitions
            var validTargets = AllStatuses
                .Where(t => WorkItemTransitionService.IsValidTransition(current, t))
                .ToArray();

            // Non-terminal state MUST have at least one valid outgoing transition
            if (validTargets.Length == 0)
                return false; // Stranded! This should never happen.

            // Take a random valid transition
            current = validTargets[rng.Next(validTargets.Length)];
            steps++;
        }

        // If we hit maxSteps, we're in a cycle (Dispatched↔Pending).
        // That's valid — the system can re-queue indefinitely.
        // Verify current state is non-terminal (otherwise it would have exited above).
        return !TerminalStatuses.Contains(current);
    }

    /// <summary>
    /// Property: Every non-terminal state has at least one path to a terminal state.
    /// This ensures no work item can get permanently stuck without reaching completion.
    /// </summary>
    [Fact]
    public void EveryNonTerminalState_CanReachATerminalState()
    {
        var nonTerminals = AllStatuses.Except(TerminalStatuses).ToArray();

        foreach (var start in nonTerminals)
        {
            var reachable = ComputeReachableStates(start);
            var canTerminate = reachable.Any(s => TerminalStatuses.Contains(s));

            if (!canTerminate)
                throw new Exception($"State {start} has no path to any terminal state!");
        }
    }

    /// <summary>
    /// Property: Terminal states are truly terminal — no valid outgoing transitions.
    /// </summary>
    [Fact]
    public void TerminalStates_HaveNoOutgoingTransitions()
    {
        foreach (var terminal in TerminalStatuses)
        {
            var outgoing = AllStatuses
                .Where(t => WorkItemTransitionService.IsValidTransition(terminal, t))
                .ToArray();

            if (outgoing.Length > 0)
                throw new Exception(
                    $"Terminal state {terminal} has unexpected outgoing transitions to: {string.Join(", ", outgoing)}");
        }
    }

    /// <summary>
    /// Property: The initial state (Pending) is reachable only from Dispatched (re-queue scenario).
    /// No other state can transition back to Pending.
    /// </summary>
    [Fact]
    public void Pending_OnlyReachableFrom_Dispatched()
    {
        var statesThatCanReachPending = AllStatuses
            .Where(s => s != WorkItemStatus.Pending && WorkItemTransitionService.IsValidTransition(s, WorkItemStatus.Pending))
            .ToArray();

        if (statesThatCanReachPending.Length != 1 || statesThatCanReachPending[0] != WorkItemStatus.Dispatched)
            throw new Exception(
                $"Expected only Dispatched→Pending, but found: {string.Join(", ", statesThatCanReachPending)}");
    }

    private static HashSet<WorkItemStatus> ComputeReachableStates(WorkItemStatus start)
    {
        var visited = new HashSet<WorkItemStatus>();
        var queue = new Queue<WorkItemStatus>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var target in AllStatuses)
            {
                if (WorkItemTransitionService.IsValidTransition(current, target) && visited.Add(target))
                    queue.Enqueue(target);
            }
        }

        return visited;
    }
}

/// <summary>
/// Compile-time guard: asserts that WorkItemStatus terminal-state ordinals match
/// the partial unique index filter in PipelineDbContext's OnModelCreating.
/// Note: Detailed ordinal assertions live in WorkItemStatusOrdinalStabilityTests.cs.
/// This guard provides a minimal check co-located with the property tests.
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
}
