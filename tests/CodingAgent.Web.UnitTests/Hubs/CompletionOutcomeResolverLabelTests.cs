using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Pure unit tests for <see cref="CompletionOutcomeResolver.ResolveAgentLabel"/>.
/// No mocks required — the method is a pure function.
/// Added as part of issue #3261 (centralise terminal-outcome-to-agent-label mapping).
/// </summary>
public class CompletionOutcomeResolverLabelTests
{
    // ── Outcome-based fallback (no FinalLabel) ────────────────────────────

    [Fact]
    public void ResolveAgentLabel_Succeeded_NoFinalLabel_ReturnsAgentDone()
    {
        var label = CompletionOutcomeResolver.ResolveAgentLabel(WorkItemStatus.Succeeded, finalLabel: null);

        label.Should().Be(AgentLabels.Done);
    }

    [Fact]
    public void ResolveAgentLabel_Failed_NoFinalLabel_ReturnsAgentError()
    {
        var label = CompletionOutcomeResolver.ResolveAgentLabel(WorkItemStatus.Failed, finalLabel: null);

        label.Should().Be(AgentLabels.Error);
    }

    [Fact]
    public void ResolveAgentLabel_Cancelled_NoFinalLabel_ReturnsAgentCancelled()
    {
        var label = CompletionOutcomeResolver.ResolveAgentLabel(WorkItemStatus.Cancelled, finalLabel: null);

        label.Should().Be(AgentLabels.Cancelled);
    }

    [Fact]
    public void ResolveAgentLabel_UnrecognisedStatus_ReturnsNull()
    {
        // Any status value outside the three known terminal arms returns null so the caller
        // can skip the label swap (matches RunTerminalCleanupAsync null-skip behaviour).
        var label = CompletionOutcomeResolver.ResolveAgentLabel((WorkItemStatus)999, finalLabel: null);

        label.Should().BeNull();
    }

    // ── FinalLabel precedence (valid label overrides outcome) ─────────────

    [Fact]
    public void ResolveAgentLabel_Succeeded_ValidFinalLabel_ReturnsFinalLabel()
    {
        // A known agent label overrides the outcome-based default.
        var label = CompletionOutcomeResolver.ResolveAgentLabel(
            WorkItemStatus.Succeeded, finalLabel: AgentLabels.NeedsRefinement);

        label.Should().Be(AgentLabels.NeedsRefinement);
    }

    [Fact]
    public void ResolveAgentLabel_Failed_ValidFinalLabel_ReturnsFinalLabel()
    {
        // FinalLabel override works regardless of the terminal status arm.
        var label = CompletionOutcomeResolver.ResolveAgentLabel(
            WorkItemStatus.Failed, finalLabel: AgentLabels.NeedsRefinement);

        label.Should().Be(AgentLabels.NeedsRefinement);
    }

    [Fact]
    public void ResolveAgentLabel_Cancelled_ValidFinalLabel_ReturnsFinalLabel()
    {
        var label = CompletionOutcomeResolver.ResolveAgentLabel(
            WorkItemStatus.Cancelled, finalLabel: AgentLabels.WontDo);

        label.Should().Be(AgentLabels.WontDo);
    }

    // ── Invalid FinalLabel falls back to outcome ──────────────────────────

    [Fact]
    public void ResolveAgentLabel_Succeeded_InvalidFinalLabel_FallsBackToAgentDone()
    {
        // A string that is not in AgentLabels.All is treated as absent and the
        // outcome-based fallback applies.
        var label = CompletionOutcomeResolver.ResolveAgentLabel(
            WorkItemStatus.Succeeded, finalLabel: "not-an-agent-label");

        label.Should().Be(AgentLabels.Done);
    }

    [Fact]
    public void ResolveAgentLabel_Failed_InvalidFinalLabel_FallsBackToAgentError()
    {
        var label = CompletionOutcomeResolver.ResolveAgentLabel(
            WorkItemStatus.Failed, finalLabel: "some-unknown-label");

        label.Should().Be(AgentLabels.Error);
    }

    // ── All known AgentLabels.All members are accepted ────────────────────

    [Theory]
    [MemberData(nameof(AllAgentLabels))]
    public void ResolveAgentLabel_AnyKnownAgentLabel_IsHonoured(string knownLabel)
    {
        // Every label in AgentLabels.All must be returned as-is when passed as finalLabel,
        // regardless of the terminal status. This guards against AgentLabels.All growing a new
        // entry that the FinalLabel precedence logic does not honour.
        var label = CompletionOutcomeResolver.ResolveAgentLabel(WorkItemStatus.Succeeded, knownLabel);

        label.Should().Be(knownLabel,
            $"'{knownLabel}' is in AgentLabels.All and must be honoured over the outcome fallback");
    }

    public static IEnumerable<object[]> AllAgentLabels()
        => AgentLabels.All.Select(l => new object[] { l });
}
