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

    // ── FilterForIssueCreation case-insensitivity (issue #3337) ───────────

    /// <summary>
    /// A mixed-case gated label must be recognised as an agent label and dropped.
    /// Before the fix: All uses an ordinal List, so "Agent:Epic-Approved" is not found
    /// and passes through — this test was failing.
    /// After the fix: All uses OrdinalIgnoreCase, so the label is correctly dropped.
    /// </summary>
    [Fact]
    public void FilterForIssueCreation_MixedCaseGatedLabel_IsDropped()
    {
        var result = AgentLabels.FilterForIssueCreation(["Agent:Epic-Approved", "backend"]);
        result.Should().NotContain("Agent:Epic-Approved",
            because: "mixed-case gated labels must be treated as agent labels and dropped");
        result.Should().Contain("backend");
    }

    [Fact]
    public void FilterForIssueCreation_MixedCaseTerminalLabel_IsDropped()
    {
        var result = AgentLabels.FilterForIssueCreation(["Agent:Done", "feature"]);
        result.Should().NotContain("Agent:Done",
            because: "mixed-case terminal labels must be treated as agent labels and dropped");
        result.Should().Contain("feature");
    }

    /// <summary>
    /// agent:next in any casing must be kept — it is in AllowedOnCreation.
    /// </summary>
    [Fact]
    public void FilterForIssueCreation_MixedCaseAllowedLabel_IsKept()
    {
        var result = AgentLabels.FilterForIssueCreation(["AGENT:NEXT", "backend"]);
        result.Should().Contain("AGENT:NEXT",
            because: "mixed-case agent:next is allowed on creation and must be forwarded");
        result.Should().Contain("backend");
        // TODO: This test does not assert that the original label casing is preserved in the output.
        // FilterForIssueCreation returns the input string as-is (no normalisation), so the returned
        // list contains "AGENT:NEXT" — not "agent:next". An explicit string-identity assertion
        // (e.g. result.Should().ContainSingle(l => l == "AGENT:NEXT")) would pin this contract so a
        // future refactor that normalises casing does not silently break callers.
    }

    /// <summary>
    /// agent:generated in any casing must be kept — it is in AllowedOnCreation.
    /// </summary>
    [Fact]
    public void FilterForIssueCreation_MixedCaseGeneratedLabel_IsKept()
    {
        var result = AgentLabels.FilterForIssueCreation(["Agent:Generated", "backend"]);
        result.Should().Contain("Agent:Generated",
            because: "mixed-case agent:generated is allowed on creation and must be forwarded");
        result.Should().Contain("backend");
    }
}
