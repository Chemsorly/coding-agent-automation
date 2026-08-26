using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentSelectorKey"/>.
/// </summary>
public sealed class AgentSelectorKeyTests
{
    // ── Null / empty inputs ───────────────────────────────────────────────

    [Fact]
    public void From_NullLabels_ReturnsEmpty()
        => AgentSelectorKey.From(null).Should().Be(string.Empty);

    [Fact]
    public void From_EmptyEnumerable_ReturnsEmpty()
        => AgentSelectorKey.From([]).Should().Be(string.Empty);

    // ── Single label ──────────────────────────────────────────────────────

    [Fact]
    public void From_SingleLabel_ReturnsThatLabel()
        => AgentSelectorKey.From(["dotnet"]).Should().Be("dotnet");

    // ── Multiple labels — ordinal sort ────────────────────────────────────

    [Fact]
    public void From_MultipleLabels_SortedOrdinallyAndJoinedWithComma()
    {
        var result = AgentSelectorKey.From(["kiro", "dotnet", "dotnet10"]);
        // Ordinal sort: 'd' < 'k', "dotnet" < "dotnet10"
        result.Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void From_AlreadySortedLabels_SameResult()
    {
        var sorted = new[] { "dotnet", "dotnet10", "kiro" };
        AgentSelectorKey.From(sorted).Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void From_ReverseSortedLabels_NormalizesToSortedKey()
    {
        var reversed = new[] { "kiro", "dotnet10", "dotnet" };
        AgentSelectorKey.From(reversed).Should().Be("dotnet,dotnet10,kiro");
    }

    // ── Idempotency: applying From twice gives same result ────────────────

    [Fact]
    public void From_AppliedTwice_Idempotent()
    {
        var labels = new[] { "python312", "kiro", "dotnet" };
        var first = AgentSelectorKey.From(labels);
        var second = AgentSelectorKey.From(first.Split(','));
        second.Should().Be(first, "canonical key must be stable under round-trip split");
    }

    // ── Ordinal sort is case-sensitive (uppercase < lowercase in ASCII) ───

    [Fact]
    public void From_MixedCase_OrdinalSortApplied()
    {
        // Ordinal: 'A'(65) < 'a'(97), so uppercase labels sort before lowercase
        var result = AgentSelectorKey.From(["beta", "Alpha"]);
        result.Should().Be("Alpha,beta");
    }

    // ── Separator is comma (not semicolon, not space) ─────────────────────

    [Fact]
    public void From_TwoLabels_SeparatedByComma()
    {
        var result = AgentSelectorKey.From(["a", "b"]);
        result.Should().Contain(",");
        result.Should().NotContain(";");
        result.Should().NotContain(" ");
    }

    // ── Various label sets ────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { "opencode", "java21", "java" }, "java,java21,opencode")]
    [InlineData(new[] { "z", "a", "m" }, "a,m,z")]
    [InlineData(new[] { "kiro" }, "kiro")]
    [InlineData(new[] { "b", "a" }, "a,b")]
    public void From_VariousInputs_ProducesExpectedKey(string[] labels, string expected)
        => AgentSelectorKey.From(labels).Should().Be(expected);

    // ── Output count matches distinct input labels ────────────────────────

    [Fact]
    public void From_ThreeDistinctLabels_OutputHasThreeParts()
    {
        var result = AgentSelectorKey.From(["x", "y", "z"]);
        result.Split(',').Should().HaveCount(3);
    }

    [Fact]
    public void From_TwoLabels_OutputHasTwoParts()
    {
        var result = AgentSelectorKey.From(["a", "b"]);
        result.Split(',').Should().HaveCount(2);
    }
}
