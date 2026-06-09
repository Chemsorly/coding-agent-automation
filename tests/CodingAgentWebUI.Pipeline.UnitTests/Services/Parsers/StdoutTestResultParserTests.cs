using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class StdoutTestResultParserTests
{
    [Fact]
    public void ParseTestCounts_NullInput_ReturnsZeros()
    {
        StdoutTestResultParser.ParseTestCounts(null!).Should().Be((0, 0, 0));
    }

    [Fact]
    public void ParseTestCounts_EmptyInput_ReturnsZeros()
    {
        StdoutTestResultParser.ParseTestCounts("").Should().Be((0, 0, 0));
    }

    [Fact]
    public void ParseTestCounts_WhitespaceInput_ReturnsZeros()
    {
        StdoutTestResultParser.ParseTestCounts("   \n\t  ").Should().Be((0, 0, 0));
    }

    [Fact]
    public void ParseTestCounts_DotNet10Summary_ParsesCorrectly()
    {
        var output = "Test summary: total: 47; failed: 2; succeeded: 42; skipped: 3";
        var result = StdoutTestResultParser.ParseTestCounts(output);
        result.Should().Be((42, 2, 3));
    }

    [Fact]
    public void ParseTestCounts_PytestFormat_ParsesCorrectly()
    {
        var output = "========================= 5 passed, 2 failed, 1 skipped in 3.45s =========================";
        var result = StdoutTestResultParser.ParseTestCounts(output);
        result.Should().Be((5, 2, 1));
    }

    [Fact]
    public void ParseTestCounts_PytestWithErrors_AddsErrorsToFailed()
    {
        var output = "========================= 3 passed, 1 failed, 2 error in 1.23s =========================";
        var result = StdoutTestResultParser.ParseTestCounts(output);
        result.Should().Be((3, 3, 0));
    }

    [Fact]
    public void ParseTestCounts_MavenFormat_ParsesCorrectly()
    {
        var output = "Tests run: 10, Failures: 2, Errors: 1, Skipped: 3";
        var result = StdoutTestResultParser.ParseTestCounts(output);
        // passed = 10 - 2 - 1 - 3 = 4, failed = 2 + 1 = 3
        result.Should().Be((4, 3, 3));
    }

    [Fact]
    public void ParseTestCounts_MavenMultipleModules_Aggregates()
    {
        var output = """
            Tests run: 5, Failures: 1, Errors: 0, Skipped: 0
            Tests run: 3, Failures: 0, Errors: 1, Skipped: 1
            """;
        var result = StdoutTestResultParser.ParseTestCounts(output);
        // Module1: passed=4, failed=1, skip=0; Module2: passed=1, failed=1, skip=1
        result.Should().Be((5, 2, 1));
    }

    [Fact]
    public void ParseTestCounts_DotNetPerAssemblyFormat_Aggregates()
    {
        var output = """
            Passed:  10, Failed:   2, Skipped:   1
            Passed:   5, Failed:   0, Skipped:   3
            """;
        var result = StdoutTestResultParser.ParseTestCounts(output);
        result.Should().Be((15, 2, 4));
    }

    [Fact]
    public void ParseTestCounts_NoMatchingPattern_ReturnsZeros()
    {
        var output = "Some random build output with no test results";
        StdoutTestResultParser.ParseTestCounts(output).Should().Be((0, 0, 0));
    }

    [Property(MaxTest = 20)]
    public Property ParseTestCounts_RoundTrip_DotNet10Summary()
    {
        var gen =
            from passed in Gen.Choose(0, 500)
            from failed in Gen.Choose(0, 500)
            from skipped in Gen.Choose(0, 500)
            select (passed, failed, skipped);

        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var total = t.passed + t.failed + t.skipped;
            var output = $"Test summary: total: {total}; failed: {t.failed}; succeeded: {t.passed}; skipped: {t.skipped}";
            var result = StdoutTestResultParser.ParseTestCounts(output);
            result.Should().Be((t.passed, t.failed, t.skipped));
        });
    }
}
