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

    // ── DispatchIneligibleLabels membership ───────────────────────────────

    // TODO: DispatchIneligibleLabels_ContainsEpicReview is redundant with
    // TerminalLabels_IsSubsetOf_DispatchIneligibleLabels below (which is a strictly stronger
    // assertion). Consider removing this point-membership test in a future cleanup pass.
    [Fact]
    public void DispatchIneligibleLabels_ContainsEpicReview() =>
        AgentLabels.DispatchIneligibleLabels.Should().Contain(AgentLabels.EpicReview);

    [Fact]
    public void TerminalLabels_IsSubsetOf_DispatchIneligibleLabels() =>
        AgentLabels.TerminalLabels.Should().BeSubsetOf(AgentLabels.DispatchIneligibleLabels);
}
