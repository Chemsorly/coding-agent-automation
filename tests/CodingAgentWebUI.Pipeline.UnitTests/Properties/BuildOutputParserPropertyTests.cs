using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Crash-freedom property tests for BuildOutputParser.
/// This parser gates pipeline progression — if it throws or returns negative values,
/// the quality gate makes incorrect pass/fail decisions. Guards against regex ReDoS
/// and edge cases from arbitrary MSBuild output.
/// </summary>
public class BuildOutputParserPropertyTests
{
    /// <summary>
    /// For any arbitrary string input, ParseBuildErrorCounts never throws and always
    /// returns non-negative counts.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property ParseBuildErrorCounts_NeverThrows_ForAnyString()
    {
        var randomStringGen = Gen.Choose(1, 200)
            .SelectMany(len => Gen.ArrayOf(Gen.Choose(0, 127).Select(i => (char)i), len))
            .Select(chars => new string(chars));

        var adversarialGen = Gen.Elements(
            "",
            "   ",
            "\n\n\n",
            "0 Error(s)",
            "999999999999 Error(s)",
            "abc Error(s)",
            "1 Error(s) 2 Warning(s)",
            "Build succeeded.",
            "Build FAILED.",
            new string('E', 5_000) + "rror(s)",
            "((([[[{{{***+++???",
            "\\d+\\s+Error",
            "0 Error(s)\n    0 Warning(s)",
            "-1 Error(s)",
            "2147483648 Error(s)");

        var gen = Gen.OneOf(randomStringGen, adversarialGen);

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var (errors, warnings) = BuildOutputParser.ParseBuildErrorCounts(input);

            errors.Should().BeGreaterThanOrEqualTo(0,
                $"errors should be non-negative for input: [{Truncate(input)}]");
            warnings.Should().BeGreaterThanOrEqualTo(0,
                $"warnings should be non-negative for input: [{Truncate(input)}]");
        });
    }

    /// <summary>
    /// Null input returns (0, 0).
    /// </summary>
    [Fact]
    public void ParseBuildErrorCounts_Null_ReturnsZeros()
    {
        var (errors, warnings) = BuildOutputParser.ParseBuildErrorCounts(null!);
        errors.Should().Be(0);
        warnings.Should().Be(0);
    }

    /// <summary>
    /// Empty/whitespace returns (0, 0).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r")]
    public void ParseBuildErrorCounts_EmptyOrWhitespace_ReturnsZeros(string input)
    {
        var (errors, warnings) = BuildOutputParser.ParseBuildErrorCounts(input);
        errors.Should().Be(0);
        warnings.Should().Be(0);
    }

    /// <summary>
    /// Typical success output returns correct counts.
    /// </summary>
    [Fact]
    public void ParseBuildErrorCounts_TypicalSuccess_ReturnsZeroErrors()
    {
        var input = """
            Build succeeded.
                0 Warning(s)
                0 Error(s)
            """;
        var (errors, warnings) = BuildOutputParser.ParseBuildErrorCounts(input);
        errors.Should().Be(0);
        warnings.Should().Be(0);
    }

    /// <summary>
    /// Typical failure output extracts error and warning counts.
    /// </summary>
    [Fact]
    public void ParseBuildErrorCounts_TypicalFailure_ExtractsCounts()
    {
        var input = """
            Build FAILED.
                3 Warning(s)
                2 Error(s)
            """;
        var (errors, warnings) = BuildOutputParser.ParseBuildErrorCounts(input);
        errors.Should().Be(2);
        warnings.Should().Be(3);
    }

    /// <summary>
    /// ReDoS resistance: very long repetitive inputs complete within bounded time.
    /// </summary>
    [Fact]
    public void ParseBuildErrorCounts_LongRepetitiveInput_CompletesQuickly()
    {
        var input = string.Concat(Enumerable.Repeat("Error(s) Warning(s) ", 5000));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        BuildOutputParser.ParseBuildErrorCounts(input);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "parser should not catastrophically backtrack on adversarial input");
    }

    /// <summary>
    /// Text with no recognizable pattern returns (0, 0).
    /// </summary>
    [Property(MaxTest = 20)]
    public Property ParseBuildErrorCounts_NoPattern_ReturnsZeros()
    {
        var gen = Gen.Elements(
            "hello world", "npm test passed", "jest --coverage",
            "PASS src/app.test.js", "All specs passed");

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var (errors, warnings) = BuildOutputParser.ParseBuildErrorCounts(input);
            (errors + warnings).Should().Be(0);
        });
    }

    private static string Truncate(string? input, int maxLen = 50)
    {
        if (input is null) return "<null>";
        if (input.Length <= maxLen) return input;
        return input[..maxLen] + "...";
    }
}
