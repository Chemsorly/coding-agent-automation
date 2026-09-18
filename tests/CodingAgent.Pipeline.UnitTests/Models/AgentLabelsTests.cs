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
