using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="LabelStateMachine"/>.
/// Verifies the transition map is correct, complete, and that validation works as expected.
/// </summary>
public class LabelStateMachineTests
{
    // ── Valid transition tests ────────────────────────────────────────────

    [Theory]
    [InlineData(AgentLabels.Next, AgentLabels.InProgress)]
    [InlineData(AgentLabels.InProgress, AgentLabels.Done)]
    [InlineData(AgentLabels.InProgress, AgentLabels.Error)]
    [InlineData(AgentLabels.InProgress, AgentLabels.Cancelled)]
    [InlineData(AgentLabels.InProgress, AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.InProgress, AgentLabels.WontDo)]
    [InlineData(AgentLabels.InProgress, AgentLabels.EpicReview)]
    [InlineData(AgentLabels.InProgress, AgentLabels.Next)]
    [InlineData(AgentLabels.InProgress, AgentLabels.Epic)]
    [InlineData(AgentLabels.InProgress, AgentLabels.EpicApproved)]
    [InlineData(AgentLabels.Error, AgentLabels.Next)]
    [InlineData(AgentLabels.Error, AgentLabels.InProgress)]
    [InlineData(AgentLabels.NeedsRefinement, AgentLabels.Next)]
    [InlineData(AgentLabels.NeedsRefinement, AgentLabels.InProgress)]
    [InlineData(AgentLabels.Cancelled, AgentLabels.Next)]
    [InlineData(AgentLabels.Cancelled, AgentLabels.InProgress)]
    [InlineData(AgentLabels.Done, AgentLabels.Next)]
    [InlineData(AgentLabels.Epic, AgentLabels.InProgress)]
    [InlineData(AgentLabels.EpicReview, AgentLabels.EpicApproved)]
    [InlineData(AgentLabels.EpicReview, AgentLabels.Cancelled)]
    [InlineData(AgentLabels.EpicApproved, AgentLabels.InProgress)]
    public void IsValidTransition_ValidTransition_ReturnsTrue(string current, string target)
    {
        LabelStateMachine.IsValidTransition(current, target).Should().BeTrue(
            $"Transition {current} → {target} should be valid");
    }

    // ── Invalid transition tests ─────────────────────────────────────────

    [Theory]
    [InlineData(AgentLabels.Next, AgentLabels.Error)]
    [InlineData(AgentLabels.Next, AgentLabels.Done)]
    [InlineData(AgentLabels.Next, AgentLabels.Cancelled)]
    [InlineData(AgentLabels.Next, AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.Done, AgentLabels.InProgress)]
    [InlineData(AgentLabels.Done, AgentLabels.Error)]
    [InlineData(AgentLabels.Done, AgentLabels.Done)]
    [InlineData(AgentLabels.EpicApproved, AgentLabels.Done)]
    [InlineData(AgentLabels.EpicApproved, AgentLabels.Error)]
    [InlineData(AgentLabels.Epic, AgentLabels.Done)]
    [InlineData(AgentLabels.Epic, AgentLabels.Error)]
    [InlineData(AgentLabels.EpicReview, AgentLabels.InProgress)]
    [InlineData(AgentLabels.EpicReview, AgentLabels.Done)]
    public void IsValidTransition_InvalidTransition_ReturnsFalse(string current, string target)
    {
        LabelStateMachine.IsValidTransition(current, target).Should().BeFalse(
            $"Transition {current} → {target} should be invalid");
    }

    // ── Null/unknown current label tests ─────────────────────────────────

    [Theory]
    [InlineData(AgentLabels.InProgress)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.Done)]
    [InlineData(AgentLabels.Next)]
    public void IsValidTransition_NullCurrentLabel_AlwaysReturnsTrue(string target)
    {
        LabelStateMachine.IsValidTransition(null, target).Should().BeTrue(
            "Null current label means validation is skipped — always valid");
    }

    [Theory]
    [InlineData("unknown-label", AgentLabels.InProgress)]
    [InlineData("agent:nonexistent", AgentLabels.Done)]
    [InlineData("", AgentLabels.Next)]
    public void IsValidTransition_UnknownCurrentLabel_ReturnsFalse(string current, string target)
    {
        LabelStateMachine.IsValidTransition(current, target).Should().BeFalse(
            "Unknown current label not in state machine should return false");
    }

    // ── ValidateTransition does not throw ─────────────────────────────────

    [Fact]
    public void ValidateTransition_InvalidTransition_DoesNotThrow()
    {
        // ValidateTransition must never throw — it's fail-open by design
        // TODO: Verify that a warning is actually logged when an invalid transition is detected (#1046)
        var act = () => LabelStateMachine.ValidateTransition(AgentLabels.Next, AgentLabels.Error);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateTransition_ValidTransition_DoesNotThrow()
    {
        // TODO: This test is tautological — ValidateTransition never throws for any input; verify logging behavior instead (#1046)
        var act = () => LabelStateMachine.ValidateTransition(AgentLabels.Next, AgentLabels.InProgress);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateTransition_NullCurrentLabel_DoesNotThrow()
    {
        var act = () => LabelStateMachine.ValidateTransition(null, AgentLabels.Error);
        act.Should().NotThrow();
    }

    // ── Production code path verification ────────────────────────────────
    // Each test case maps to a real code path documented in the analysis.

    [Theory]
    // RunLifecycleManager.AgentAcceptedRunAsync: next → in-progress
    [InlineData(AgentLabels.Next, AgentLabels.InProgress, "RunLifecycleManager.AgentAcceptedRunAsync")]
    // RunLifecycleManager.FailRunAsync: in-progress → error
    [InlineData(AgentLabels.InProgress, AgentLabels.Error, "RunLifecycleManager.FailRunAsync")]
    // RunLifecycleManager.CancelRunAsync: in-progress → cancelled
    [InlineData(AgentLabels.InProgress, AgentLabels.Cancelled, "RunLifecycleManager.CancelRunAsync")]
    // AgentPhaseExecutor.FailPhaseAsync: in-progress → needs-refinement
    [InlineData(AgentLabels.InProgress, AgentLabels.NeedsRefinement, "AgentPhaseExecutor.FailPhaseAsync (not_ready)")]
    // AgentPhaseExecutor.FailPhaseAsync: in-progress → wont-do
    [InlineData(AgentLabels.InProgress, AgentLabels.WontDo, "AgentPhaseExecutor.FailPhaseAsync (wont_do)")]
    // PipelineOrchestrationService: in-progress → done
    [InlineData(AgentLabels.InProgress, AgentLabels.Done, "PipelineOrchestrationService.PostPullRequestCompletionAsync")]
    // PostDecompositionPlanStep: in-progress → epic-review
    [InlineData(AgentLabels.InProgress, AgentLabels.EpicReview, "PostDecompositionPlanStep")]
    // DispatchOrchestrationService.RevertFailedDistributionAsync: in-progress → next
    [InlineData(AgentLabels.InProgress, AgentLabels.Next, "DispatchOrchestrationService.RevertFailedDistributionAsync")]
    // AgentJobDispatcher decomposition Phase 1 revert: in-progress → epic
    [InlineData(AgentLabels.InProgress, AgentLabels.Epic, "AgentJobDispatcher.DispatchDecompositionToAgentAsync (Phase 1 revert)")]
    // AgentJobDispatcher decomposition Phase 2 revert: in-progress → epic-approved
    [InlineData(AgentLabels.InProgress, AgentLabels.EpicApproved, "AgentJobDispatcher.DispatchDecompositionToAgentAsync (Phase 2 revert)")]
    // ReconciliationService: error → next
    [InlineData(AgentLabels.Error, AgentLabels.Next, "ReconciliationService.ReconcileLabelsOnStartupAsync")]
    // ReconciliationService: done → next
    [InlineData(AgentLabels.Done, AgentLabels.Next, "ReconciliationService.ReconcileLabelsOnStartupAsync (Succeeded)")]
    // ReconciliationService: cancelled → next
    [InlineData(AgentLabels.Cancelled, AgentLabels.Next, "ReconciliationService startup reconciliation")]
    // Epic decomposition dispatch: epic → in-progress
    [InlineData(AgentLabels.Epic, AgentLabels.InProgress, "AgentJobDispatcher.DispatchDecompositionToAgentAsync (Phase 1)")]
    // Epic decomposition dispatch: epic-approved → in-progress
    [InlineData(AgentLabels.EpicApproved, AgentLabels.InProgress, "AgentJobDispatcher.DispatchDecompositionToAgentAsync (Phase 2)")]
    // Human approval: epic-review → epic-approved
    [InlineData(AgentLabels.EpicReview, AgentLabels.EpicApproved, "Human approval via GitHub UI")]
    // Human cancel: epic-review → cancelled
    [InlineData(AgentLabels.EpicReview, AgentLabels.Cancelled, "Human cancels decomposition")]
    public void AllProductionTransitions_AreValidInStateMachine(string from, string to, string codePath)
    {
        LabelStateMachine.IsValidTransition(from, to).Should().BeTrue(
            $"Production code path '{codePath}' performs transition {from} → {to} which must be valid");
    }

    // ── Structural integrity tests ───────────────────────────────────────

    [Fact]
    public void GeneratedLabel_IsExcludedFromStateMachine()
    {
        LabelStateMachine.ValidTransitions.Keys
            .Should().NotContain(AgentLabels.Generated,
                "agent:generated is orthogonal to the state machine and should not appear as a source state");

        // Also verify it doesn't appear as a target in any transition set
        foreach (var (source, targets) in LabelStateMachine.ValidTransitions)
        {
            targets.Should().NotContain(AgentLabels.Generated,
                $"agent:generated should not appear as a target from {source}");
        }
    }

    [Fact]
    public void TransitionMap_CoversAllNonOrthogonalLabels()
    {
        // Every label in AgentLabels.All except Generated should appear as a key in the transition map
        var expectedKeys = AgentLabels.All
            .Where(l => l != AgentLabels.Generated)
            .ToHashSet();

        var actualKeys = LabelStateMachine.ValidTransitions.Keys.ToHashSet();

        actualKeys.Should().BeEquivalentTo(expectedKeys,
            "Every non-orthogonal agent label must have defined transitions in the state machine");
    }

    [Fact]
    public void TransitionMap_AllTargets_AreValidLabels()
    {
        var allLabels = AgentLabels.All.ToHashSet();

        foreach (var (source, targets) in LabelStateMachine.ValidTransitions)
        {
            foreach (var target in targets)
            {
                allLabels.Should().Contain(target,
                    $"Target label '{target}' in transition from '{source}' must be a valid AgentLabel");
            }
        }
    }

    [Fact]
    public void TransitionMap_AllKeys_AreValidLabels()
    {
        var allLabels = AgentLabels.All.ToHashSet();

        foreach (var key in LabelStateMachine.ValidTransitions.Keys)
        {
            allLabels.Should().Contain(key,
                $"Source label '{key}' in transition map must be a valid AgentLabel");
        }
    }

    [Fact]
    public void TransitionMap_NoSelfTransitions()
    {
        // A label should not transition to itself — that's a no-op
        foreach (var (source, targets) in LabelStateMachine.ValidTransitions)
        {
            targets.Should().NotContain(source,
                $"Self-transition {source} → {source} should not be in the valid map");
        }
    }

    [Fact]
    public void TransitionMap_InProgressHasMostOutgoingTransitions()
    {
        // agent:in-progress is the active working state — it should have the most outgoing transitions
        var inProgressCount = LabelStateMachine.ValidTransitions[AgentLabels.InProgress].Count;

        foreach (var (source, targets) in LabelStateMachine.ValidTransitions)
        {
            if (source == AgentLabels.InProgress) continue;
            targets.Count.Should().BeLessThanOrEqualTo(inProgressCount,
                $"No state should have more outgoing transitions than agent:in-progress (got {targets.Count} for {source})");
        }
    }
}
