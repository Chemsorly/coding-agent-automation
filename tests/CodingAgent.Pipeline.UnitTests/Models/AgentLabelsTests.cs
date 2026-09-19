using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="AgentLabels"/> set membership.
/// Locks in the contract for TerminalLabels and DispatchIneligibleLabels.
/// </summary>
public class AgentLabelsTests
{
    // ── TerminalLabels membership ──────────────────────────────────────────

    [Theory]
    [InlineData(AgentLabels.Done)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.WontDo)]
    [InlineData(AgentLabels.Cancelled)]
    [InlineData(AgentLabels.EpicReview)]
    public void TerminalLabels_ContainsMember(string label) =>
        AgentLabels.TerminalLabels.Should().Contain(label);

    // ── DispatchIneligibleLabels regression guard ──────────────────────────

    // TODO: TerminalLabels_IsSubsetOf_DispatchIneligibleLabels was removed when EpicReview was
    // intentionally removed from DispatchIneligibleLabels. That test was the only structural guard
    // ensuring all terminal labels are also dispatch-ineligible. With EpicReview now in TerminalLabels
    // but not in DispatchIneligibleLabels, the subset invariant no longer holds universally.
    // Consider replacing the removed test with one that explicitly documents which TerminalLabels are
    // intentionally absent from DispatchIneligibleLabels (currently only EpicReview), so that future
    // label additions to TerminalLabels that accidentally miss DispatchIneligibleLabels are caught.
    [Fact]
    public void DispatchIneligibleLabels_DoesNotContainEpicReview() =>
        AgentLabels.DispatchIneligibleLabels.Should().NotContain(AgentLabels.EpicReview);

    // ── DualLabelResolutionPrecedence data-integrity guard ─────────────────

    /// <summary>
    /// DualLabelResolutionPrecedence must contain every label in AgentLabels.All
    /// except agent:generated (which is orthogonal and intentionally excluded).
    /// This prevents future label additions from being silently skipped by the
    /// dual-label sweep.
    /// </summary>
    [Fact]
    public void DualLabelResolutionPrecedence_ContainsAllNonGeneratedLabels()
    {
        var expectedLabels = AgentLabels.All
            .Where(l => l != AgentLabels.Generated)
            .ToList();

        foreach (var label in expectedLabels)
        {
            AgentLabels.DualLabelResolutionPrecedence.Should().Contain(label,
                because: $"{label} is in AgentLabels.All (non-generated) and must appear in DualLabelResolutionPrecedence");
        }
    }

    [Fact]
    public void DualLabelResolutionPrecedence_DoesNotContainGenerated() =>
        AgentLabels.DualLabelResolutionPrecedence.Should().NotContain(AgentLabels.Generated,
            because: "agent:generated is orthogonal and must not be treated as a status label by the dual-label sweep");
}
