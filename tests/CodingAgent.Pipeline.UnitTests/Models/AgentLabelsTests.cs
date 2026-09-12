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
}
