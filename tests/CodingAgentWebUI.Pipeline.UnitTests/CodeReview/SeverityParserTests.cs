using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;

namespace CodingAgentWebUI.Pipeline.UnitTests.CodeReview;

/// <summary>
/// Unit tests for <see cref="SeverityParser.Parse"/>.
/// </summary>
public sealed class SeverityParserTests
{
    // ── Null guard ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullLines_ThrowsArgumentNullException()
    {
        var act = () => SeverityParser.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Empty input ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyList_ReturnsZeroCounts()
    {
        var result = SeverityParser.Parse([]);
        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    // ── Counting ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SingleCritical_CountsOne()
    {
        var result = SeverityParser.Parse(["[CRITICAL] serious bug in auth"]);
        result.Critical.Should().Be(1);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Fact]
    public void Parse_MultipleMarkersOnOneLine_CountsAll()
    {
        // Two [WARNING] markers on same line
        var result = SeverityParser.Parse(["[WARNING] first [WARNING] second"]);
        result.Warning.Should().Be(2);
    }

    [Fact]
    public void Parse_MixedSeverities_CountsEachCorrectly()
    {
        var lines = new[]
        {
            "[CRITICAL] auth bypass",
            "[WARNING] null ref",
            "[WARNING] race condition",
            "[SUGGESTION] rename method"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(1);
        result.Warning.Should().Be(2);
        result.Suggestion.Should().Be(1);
    }

    // ── Case insensitivity ────────────────────────────────────────────────

    [Theory]
    [InlineData("[critical]")]
    [InlineData("[Critical]")]
    [InlineData("[CRITICAL]")]
    public void Parse_CriticalCaseInsensitive_CountsOne(string marker)
    {
        var result = SeverityParser.Parse([$"{marker} issue"]);
        result.Critical.Should().Be(1);
    }

    // ── RESOLVED lines are excluded ───────────────────────────────────────

    [Fact]
    public void Parse_ResolvedLine_IsExcludedFromCounts()
    {
        var result = SeverityParser.Parse(["RESOLVED [CRITICAL] old auth bypass"]);
        result.Critical.Should().Be(0);
    }

    [Fact]
    public void Parse_ResolvedCaseInsensitive_IsExcluded()
    {
        var result = SeverityParser.Parse(["resolved [WARNING] old issue"]);
        result.Warning.Should().Be(0);
    }

    [Fact]
    public void Parse_MixedResolvedAndActive_OnlyCountsActive()
    {
        var lines = new[]
        {
            "RESOLVED [CRITICAL] old issue",
            "[WARNING] new issue"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(1);
    }

    // ── Non-marker lines ──────────────────────────────────────────────────

    [Fact]
    public void Parse_LinesWithoutMarkers_CountsZero()
    {
        var result = SeverityParser.Parse(["just some text", "no markers here"]);
        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }
}
