using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for LabelStateMachine.IsValidTransition and ValidateTransition.
/// Covers all defined transitions, null current label, empty target, and invalid transitions.
/// </summary>
public sealed class LabelStateMachineTests
{
    // ── IsValidTransition ─────────────────────────────────────────────────

    [Fact]
    public void IsValidTransition_NullCurrentLabel_AlwaysTrue()
    {
        LabelStateMachine.IsValidTransition(null, AgentLabels.InProgress).Should().BeTrue();
        LabelStateMachine.IsValidTransition(null, AgentLabels.Done).Should().BeTrue();
        LabelStateMachine.IsValidTransition(null, "any-label").Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EmptyTarget_AlwaysTrue()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Next, string.Empty).Should().BeTrue();
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, "").Should().BeTrue();
    }

    // ── Implementation flow ───────────────────────────────────────────────

    [Fact]
    public void IsValidTransition_Next_ToInProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Next, AgentLabels.InProgress).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_Next_ToDone_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Next, AgentLabels.Done).Should().BeFalse();
    }

    [Theory]
    [InlineData(AgentLabels.Done)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.Cancelled)]
    [InlineData(AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.WontDo)]
    [InlineData(AgentLabels.EpicReview)]
    public void IsValidTransition_InProgress_ToTerminal_IsValid(string target)
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, target).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_InProgress_ToNext_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.Next).Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_InProgress_ToSelf_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.InProgress, AgentLabels.InProgress).Should().BeFalse();
    }

    // ── Recovery transitions ──────────────────────────────────────────────

    [Theory]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.Cancelled)]
    public void IsValidTransition_RecoveryLabel_ToNext_IsValid(string current)
    {
        LabelStateMachine.IsValidTransition(current, AgentLabels.Next).Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.Cancelled)]
    public void IsValidTransition_RecoveryLabel_ToInProgress_IsValid(string current)
    {
        LabelStateMachine.IsValidTransition(current, AgentLabels.InProgress).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_Error_ToDone_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Error, AgentLabels.Done).Should().BeFalse();
    }

    // ── Epic flow ─────────────────────────────────────────────────────────

    [Fact]
    public void IsValidTransition_Epic_ToInProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Epic, AgentLabels.InProgress).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_Epic_ToDone_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Epic, AgentLabels.Done).Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_EpicReview_ToEpicApproved_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicReview, AgentLabels.EpicApproved).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EpicReview_ToCancelled_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicReview, AgentLabels.Cancelled).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EpicReview_ToDone_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicReview, AgentLabels.Done).Should().BeFalse();
    }

    [Fact]
    public void IsValidTransition_EpicApproved_ToInProgress_IsValid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicApproved, AgentLabels.InProgress).Should().BeTrue();
    }

    [Fact]
    public void IsValidTransition_EpicApproved_ToDone_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.EpicApproved, AgentLabels.Done).Should().BeFalse();
    }

    // ── Unknown current label ─────────────────────────────────────────────

    [Fact]
    public void IsValidTransition_UnknownCurrentLabel_IsInvalid()
    {
        LabelStateMachine.IsValidTransition("agent:unknown", AgentLabels.InProgress).Should().BeFalse();
    }

    // ── Done is terminal (no outgoing transitions) ────────────────────────

    [Fact]
    public void IsValidTransition_Done_ToAnything_IsInvalid()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.Done, AgentLabels.Next).Should().BeFalse();
        LabelStateMachine.IsValidTransition(AgentLabels.Done, AgentLabels.InProgress).Should().BeFalse();
    }

    // ── ValidateTransition ────────────────────────────────────────────────

    [Fact]
    public void ValidateTransition_ValidTransition_ReturnsTrue()
    {
        LabelStateMachine.ValidateTransition(AgentLabels.Next, AgentLabels.InProgress).Should().BeTrue();
    }

    [Fact]
    public void ValidateTransition_InvalidTransition_ReturnsFalse()
    {
        LabelStateMachine.ValidateTransition(AgentLabels.Done, AgentLabels.Next).Should().BeFalse();
    }

    [Fact]
    public void ValidateTransition_NullCurrent_ReturnsTrue()
    {
        LabelStateMachine.ValidateTransition(null, AgentLabels.Next).Should().BeTrue();
    }

    [Fact]
    public void ValidateTransition_WithIdentifier_ReturnsCorrectly()
    {
        LabelStateMachine.ValidateTransition(AgentLabels.Next, AgentLabels.InProgress, "GH-42").Should().BeTrue();
        LabelStateMachine.ValidateTransition(AgentLabels.Done, AgentLabels.Next, "GH-42").Should().BeFalse();
    }

    // ── ValidTransitions map is populated ────────────────────────────────

    [Fact]
    public void ValidTransitions_ContainsExpectedKeys()
    {
        LabelStateMachine.ValidTransitions.Keys.Should().Contain(AgentLabels.Next);
        LabelStateMachine.ValidTransitions.Keys.Should().Contain(AgentLabels.InProgress);
        LabelStateMachine.ValidTransitions.Keys.Should().Contain(AgentLabels.Epic);
    }
}
