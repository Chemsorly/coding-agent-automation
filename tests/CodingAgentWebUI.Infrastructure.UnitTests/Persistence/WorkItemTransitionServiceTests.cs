using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for WorkItemTransitionService.IsValidTransition (pure state machine logic).
/// TransitionAsync integration behavior is validated by Property 1 (task 3.2) against real Postgres.
/// </summary>
public class WorkItemTransitionServiceTests
{
    [Theory]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Dispatched, true)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Cancelled, true)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Running, false)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Succeeded, false)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Running, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Cancelled, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Succeeded, false)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Pending, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Succeeded, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Cancelled, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Pending, false)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Dispatched, false)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Failed, false)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Cancelled, false)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Pending, false)]
    [InlineData(WorkItemStatus.Failed, WorkItemStatus.Pending, false)]
    [InlineData(WorkItemStatus.Failed, WorkItemStatus.Running, false)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Pending, false)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Running, false)]
    public void IsValidTransition_ReturnsExpected(WorkItemStatus current, WorkItemStatus target, bool expected)
    {
        WorkItemTransitionService.IsValidTransition(current, target).Should().Be(expected);
    }

    [Fact]
    public void IsValidTransition_TerminalStates_CannotTransitionAnywhere()
    {
        var terminals = new[] { WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled };
        var allStatuses = Enum.GetValues<WorkItemStatus>();

        foreach (var terminal in terminals)
        foreach (var target in allStatuses)
        {
            WorkItemTransitionService.IsValidTransition(terminal, target).Should().BeFalse(
                $"Terminal state {terminal} should not transition to {target}");
        }
    }

    [Fact]
    public void IsValidTransition_SameState_ReturnsFalse()
    {
        // Same state is not a "valid transition" — it's handled by idempotency check in TransitionAsync
        var allStatuses = Enum.GetValues<WorkItemStatus>();
        foreach (var status in allStatuses)
        {
            WorkItemTransitionService.IsValidTransition(status, status).Should().BeFalse(
                $"Same-state {status} → {status} should return false (idempotency handled separately)");
        }
    }
}
