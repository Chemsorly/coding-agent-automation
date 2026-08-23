using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
}
