using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentSelectorKey.From — canonical sorted comma-joined label key.
/// </summary>
public sealed class AgentSelectorKeyTests
{
    [Fact]
    public void From_NullLabels_ReturnsEmpty()
    {
        AgentSelectorKey.From(null).Should().Be(string.Empty);
    }

    [Fact]
    public void From_EmptyList_ReturnsEmpty()
    {
        AgentSelectorKey.From([]).Should().Be(string.Empty);
    }

    [Fact]
    public void From_SingleLabel_ReturnsThatLabel()
    {
        AgentSelectorKey.From(["kiro"]).Should().Be("kiro");
    }

    [Fact]
    public void From_MultipleLabels_SortsOrdinallyAndJoinsWithComma()
    {
        var result = AgentSelectorKey.From(["dotnet", "kiro", "azure"]);
        result.Should().Be("azure,dotnet,kiro");
    }

    [Fact]
    public void From_AlreadySorted_SameResult()
    {
        AgentSelectorKey.From(["a", "b", "c"]).Should().Be("a,b,c");
    }

    [Fact]
    public void From_UnsortedInput_AlwaysProducesSameKey()
    {
        var ordered = AgentSelectorKey.From(["kiro", "dotnet"]);
        var reversed = AgentSelectorKey.From(["dotnet", "kiro"]);
        ordered.Should().Be(reversed);
    }

    [Fact]
    public void From_IsCaseSensitive_OrdinalSort()
    {
        // Uppercase comes before lowercase in ordinal sort
        var result = AgentSelectorKey.From(["kiro", "Kiro"]);
        result.Should().Be("Kiro,kiro");
    }

    [Fact]
    public void From_DuplicateLabels_IncludesBoth()
    {
        // AgentSelectorKey does not deduplicate — that's the caller's responsibility
        AgentSelectorKey.From(["kiro", "kiro"]).Should().Be("kiro,kiro");
    }

    // ── Multiple labels — ordinal sort ────────────────────────────────────

    [Fact]
    public void From_MultipleLabels_SortedOrdinallyAndJoinedWithComma()
    {
        var result = AgentSelectorKey.From(["kiro", "dotnet", "dotnet10"]);
        // Ordinal sort: 'd' < 'k', "dotnet" < "dotnet10"
        result.Should().Be("dotnet,dotnet10,kiro");
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
