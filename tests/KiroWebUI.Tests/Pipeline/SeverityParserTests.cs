using AwesomeAssertions;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

public class SeverityParserTests
{
    [Fact]
    public void Parse_WithAllSeverities_ReturnsCorrectCounts()
    {
        var lines = new[]
        {
            "[CRITICAL] Missing null check in Foo.cs",
            "[WARNING] Consider using var",
            "[SUGGESTION] Rename variable x to count",
            "[CRITICAL] SQL injection in Bar.cs"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(2);
        result.Warning.Should().Be(1);
        result.Suggestion.Should().Be(1);
    }

    [Fact]
    public void Parse_WithNoMarkers_ReturnsZeros()
    {
        var lines = new[] { "All looks good.", "No issues found." };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Fact]
    public void Parse_CaseInsensitive_MatchesAllVariants()
    {
        var lines = new[]
        {
            "[critical] lowercase",
            "[Critical] mixed case",
            "[CRITICAL] uppercase"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(3);
    }

    [Fact]
    public void Parse_WithMultipleMarkersOnSameLine_CountsEach()
    {
        var lines = new[] { "[CRITICAL] issue A [CRITICAL] issue B on same line" };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(2);
    }

    [Fact]
    public void Parse_WithEmptyInput_ReturnsZeros()
    {
        var result = SeverityParser.Parse(Array.Empty<string>());

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Fact]
    public void Parse_WithMarkersInNoise_CountsCorrectly()
    {
        var lines = new[]
        {
            "Starting code review...",
            "Checking file src/Foo.cs",
            "[WARNING] Unused import on line 3",
            "Checking file src/Bar.cs",
            "[SUGGESTION] Consider extracting method",
            "Review complete."
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(1);
        result.Suggestion.Should().Be(1);
    }

    [Fact]
    public void Parse_NullInput_ThrowsArgumentNullException()
    {
        var act = () => SeverityParser.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
