using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class BuildOutputParserTests
{
    [Fact]
    public void ParseBuildErrorCounts_NullInput_ReturnsZeros()
    {
        BuildOutputParser.ParseBuildErrorCounts(null!).Should().Be((0, 0));
    }

    [Fact]
    public void ParseBuildErrorCounts_EmptyInput_ReturnsZeros()
    {
        BuildOutputParser.ParseBuildErrorCounts("").Should().Be((0, 0));
    }

    [Fact]
    public void ParseBuildErrorCounts_WhitespaceInput_ReturnsZeros()
    {
        BuildOutputParser.ParseBuildErrorCounts("   \n\t  ").Should().Be((0, 0));
    }

    [Fact]
    public void ParseBuildErrorCounts_BothErrorsAndWarnings_ParsesCorrectly()
    {
        var output = "Build succeeded.\n    3 Warning(s)\n    1 Error(s)";
        BuildOutputParser.ParseBuildErrorCounts(output).Should().Be((1, 3));
    }

    [Fact]
    public void ParseBuildErrorCounts_OnlyErrors_ReturnsZeroWarnings()
    {
        var output = "    5 Error(s)";
        BuildOutputParser.ParseBuildErrorCounts(output).Should().Be((5, 0));
    }

    [Fact]
    public void ParseBuildErrorCounts_OnlyWarnings_ReturnsZeroErrors()
    {
        var output = "    7 Warning(s)";
        BuildOutputParser.ParseBuildErrorCounts(output).Should().Be((0, 7));
    }

    [Fact]
    public void ParseBuildErrorCounts_ZeroCounts_ReturnsZeros()
    {
        var output = "    0 Warning(s)\n    0 Error(s)";
        BuildOutputParser.ParseBuildErrorCounts(output).Should().Be((0, 0));
    }

    [Fact]
    public void ParseBuildErrorCounts_NoSummaryLine_ReturnsZeros()
    {
        var output = "src/File.cs(10,5): error CS1002: ; expected";
        BuildOutputParser.ParseBuildErrorCounts(output).Should().Be((0, 0));
    }

    [Property(MaxTest = 20)]
    public Property ParseBuildErrorCounts_RoundTrip_MatchesInput()
    {
        var gen =
            from errors in Gen.Choose(0, 999)
            from warnings in Gen.Choose(0, 999)
            select (errors, warnings);

        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var output = $"    {t.warnings} Warning(s)\n    {t.errors} Error(s)";
            var result = BuildOutputParser.ParseBuildErrorCounts(output);
            result.Should().Be((t.errors, t.warnings));
        });
    }
}
