using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="LabelStateMachine"/>.
/// Verifies all valid transitions, invalid transitions, and edge cases.
/// </summary>
public class LabelStateMachineTests
{
    // ── Valid Implementation Flow Transitions ───────────────────────────────

    [Fact]
    public void IsValidTransition_Next_To_InProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Next, AgentLabels.InProgress)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_Done_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.Done)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_Error_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_Cancelled_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_NeedsRefinement_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.NeedsRefinement)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_WontDo_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.WontDo)
            .Should().BeTrue();
    }

    // ── Valid Recovery Transitions ──────────────────────────────────────────

    [Fact]
    public void IsValidTransition_Error_To_Next_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Error, AgentLabels.Next)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_Error_To_InProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Error, AgentLabels.InProgress)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_NeedsRefinement_To_Next_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.NeedsRefinement, AgentLabels.Next)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_NeedsRefinement_To_InProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.NeedsRefinement, AgentLabels.InProgress)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_Cancelled_To_Next_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Cancelled, AgentLabels.Next)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_Cancelled_To_InProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Cancelled, AgentLabels.InProgress)
            .Should().BeTrue();
    }

    // ── Valid Epic Decomposition Transitions ────────────────────────────────

    [Fact]
    public void IsValidTransition_Epic_To_InProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Epic, AgentLabels.InProgress)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_EpicReview_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.EpicReview)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EpicReview_To_EpicApproved_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicReview, AgentLabels.EpicApproved)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EpicReview_To_Cancelled_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicReview, AgentLabels.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EpicApproved_To_InProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicApproved, AgentLabels.InProgress)
            .Should().BeTrue();
    }

    // ── Invalid Transitions ────────────────────────────────────────────────

    [Fact]
    public void IsValidTransition_Done_To_InProgress_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Done, AgentLabels.InProgress)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_Next_To_Done_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Next, AgentLabels.Done)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_Next_To_Error_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Next, AgentLabels.Error)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_Done_To_Next_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Done, AgentLabels.Next)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_InProgress_To_Next_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.Next)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_Epic_To_Done_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Epic, AgentLabels.Done)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_EpicApproved_To_Done_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicApproved, AgentLabels.Done)
            .Should().BeFalse();
    }

    // ── Edge Cases ─────────────────────────────────────────────────────────

    [Fact]
    public void IsValidTransition_NullCurrentLabel_AlwaysValid()
    {
        // No current label means initial labeling — always valid
        LabelStateMachine.IsValidTransition(null, AgentLabels.InProgress)
            .Should().BeTrue();
        LabelStateMachine.IsValidTransition(null, AgentLabels.Done)
            .Should().BeTrue();
        LabelStateMachine.IsValidTransition(null, AgentLabels.Next)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EmptyTargetLabel_AlwaysValid()
    {
        // Empty target means "remove all labels" — always valid
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, string.Empty)
            .Should().BeTrue();
        LabelStateMachine.IsValidTransition(AgentLabels.Done, string.Empty)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_UnknownCurrentLabel_IsInvalid()
    {
        // A label not in the transition map has no valid targets
        LabelStateMachine.IsValidTransition("agent:unknown", AgentLabels.InProgress)
            .Should().BeFalse();
    }

    // ── ValidateTransition ─────────────────────────────────────────────────

    [Fact]
    public void ValidateTransition_ValidTransition_ReturnsTrue()
    {
        LabelStateMachine.ValidateTransition(AgentLabels.Next, AgentLabels.InProgress, "issue-42")
            .Should().BeTrue();
    }

    [Fact]
    public void ValidateTransition_InvalidTransition_ReturnsFalse()
    {
        // Invalid transition — logs warning but returns false (no throw)
        LabelStateMachine.ValidateTransition(AgentLabels.Done, AgentLabels.InProgress, "issue-42")
            .Should().BeFalse();
    }

    [Fact]
    public void ValidateTransition_NullIdentifier_DoesNotThrow()
    {
        // Should not throw even without an identifier
        LabelStateMachine.ValidateTransition(AgentLabels.Done, AgentLabels.InProgress)
            .Should().BeFalse();
    }

    // ── Transition Map Coverage ────────────────────────────────────────────

    [Fact]
    public void ValidTransitions_AllKeysAreKnownAgentLabels()
    {
        // Every key in the transition map should be a known agent label
        foreach (var key in LabelStateMachine.ValidTransitions.Keys)
        {
            AgentLabels.All.Should().Contain(key,
                because: $"transition source '{key}' should be a known agent label");
        }
    }

    [Fact]
    public void ValidTransitions_AllTargetsAreKnownAgentLabels()
    {
        // Every target in the transition map should be a known agent label
        foreach (var (source, targets) in LabelStateMachine.ValidTransitions)
        {
            foreach (var target in targets)
            {
                AgentLabels.All.Should().Contain(target,
                    because: $"transition target '{target}' (from '{source}') should be a known agent label");
            }
        }
    }

    [Fact]
    public void ValidTransitions_NoSelfTransitions()
    {
        // A label should never transition to itself
        foreach (var (source, targets) in LabelStateMachine.ValidTransitions)
        {
            targets.Should().NotContain(source,
                because: $"'{source}' should not have a self-transition");
        }
    }

    // ── Production Code Path Coverage ──────────────────────────────────────
    // These tests verify that each known production code path produces a valid transition.
    // TODO: This test is tautological — it re-asserts the static transition map against itself.
    //       It does NOT verify that actual production callers produce transitions matching the map.
    //       Consider tracing label arguments from real call-sites or integration tests.
    // TODO: Some InlineData entries below (e.g. "Human retry", "Human refines and re-queues")
    //       represent human-initiated transitions that bypass the system entirely. Listing them
    //       as "production code paths" is misleading. Consider separating into distinct test methods
    //       (e.g., ProductionCodePaths vs HumanInitiatedTransitions).
    // TODO: No production code currently calls the new SwapLabelAsync overload with a non-null
    //       expectedCurrentLabel — the validation integration is dormant at runtime. Consider
    //       adopting the overload in at least one caller (e.g., RunLifecycleManager.AgentAcceptedRunAsync)
    //       to validate the full integration path.

    [Theory]
    [InlineData(null, "agent:next")]               // Initial dispatch: no label → agent:next
    [InlineData("agent:next", "agent:in-progress")]  // AgentAccepted: agent:next → agent:in-progress
    [InlineData("agent:in-progress", "agent:done")]  // Pipeline success
    [InlineData("agent:in-progress", "agent:error")] // QG exhausted / runtime error
    [InlineData("agent:in-progress", "agent:cancelled")] // Manual cancel / shutdown
    [InlineData("agent:in-progress", "agent:needs-refinement")] // Analysis: not_ready
    [InlineData("agent:in-progress", "agent:wont-do")]  // Analysis: wont_do
    [InlineData("agent:in-progress", "agent:epic-review")] // Decomposition Phase 1 complete
    [InlineData("agent:epic", "agent:in-progress")]  // Epic dispatch
    [InlineData("agent:epic-review", "agent:epic-approved")] // Human approves plan
    [InlineData("agent:epic-approved", "agent:in-progress")] // Phase 2 dispatched
    [InlineData("agent:error", "agent:next")]        // Human retry
    [InlineData("agent:error", "agent:in-progress")] // Direct re-dispatch from error
    [InlineData("agent:needs-refinement", "agent:next")] // Human refines and re-queues
    [InlineData("agent:cancelled", "agent:next")]    // Human re-queues after cancel
    public void ProductionCodePaths_AreAllValidTransitions(string? currentLabel, string targetLabel)
    {
        LabelStateMachine.IsValidTransition(currentLabel, targetLabel)
            .Should().BeTrue(
                because: $"the transition '{currentLabel ?? "(none)"}' → '{targetLabel}' is a known production code path");
    }
}
