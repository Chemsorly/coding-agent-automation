using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Crash-freedom property tests for StdoutTestResultParser and IssueReferenceParser.
/// These parsers run on every pipeline execution against untrusted agent output.
/// Verifies: no valid (or adversarial) input can cause an unhandled exception,
/// and output invariants hold across all inputs.
/// </summary>
public class ParserCrashFreedomPropertyTests
{
    // ── StdoutTestResultParser ────────────────────────────────────────────

    /// <summary>
    /// For any arbitrary string input, ParseTestCounts never throws and always
    /// returns non-negative counts.
    /// </summary>
    // TODO: Consider increasing MaxTest for crash-freedom tests — 20 iterations provides limited fuzzing coverage for random string inputs (previously 200).
    [Property(MaxTest = 20)]
    public Property ParseTestCounts_NeverThrows_ForAnyString()
    {
        var randomStringGen = Gen.Choose(1, 200)
            .SelectMany(len => Gen.ArrayOf(Gen.Choose(0, 127).Select(i => (char)i), len))
            .Select(chars => new string(chars));

        var adversarialGen = Gen.Elements(
            "",
            "   ",
            "\n\n\n",
            "Test summary: total: abc; failed: xyz; succeeded: ; skipped: NaN",
            "Passed: -1, Failed: -2, Skipped: -3",
            "Tests run: 0, Failures: 0, Errors: 0, Skipped: 0",
            "====== 999999999999 passed in 0.01s ======",
            new string('X', 5_000),
            "Test summary: total: 2147483647; failed: 2147483647; succeeded: 2147483647; skipped: 2147483647",
            "Passed: 999999999999, Failed: 999999999999, Skipped: 999999999999",
            "((([[[{{{***+++???",
            "\\d+\\s+\\w+",
            "Test summary: total: ; failed: ; succeeded: ; skipped: ");

        var gen = Gen.OneOf(randomStringGen, adversarialGen);

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var (passed, failed, skipped) = StdoutTestResultParser.ParseTestCounts(input);

            passed.Should().BeGreaterThanOrEqualTo(0,
                $"passed count should be non-negative for input: [{Truncate(input)}]");
            failed.Should().BeGreaterThanOrEqualTo(0,
                $"failed count should be non-negative for input: [{Truncate(input)}]");
            skipped.Should().BeGreaterThanOrEqualTo(0,
                $"skipped count should be non-negative for input: [{Truncate(input)}]");
        });
    }

    /// <summary>
    /// Null input returns (0, 0, 0).
    /// </summary>
    [Fact]
    public void ParseTestCounts_Null_ReturnsZeros()
    {
        var (passed, failed, skipped) = StdoutTestResultParser.ParseTestCounts(null!);
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    /// <summary>
    /// Empty/whitespace returns (0, 0, 0).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r")]
    public void ParseTestCounts_EmptyOrWhitespace_ReturnsZeros(string input)
    {
        var (passed, failed, skipped) = StdoutTestResultParser.ParseTestCounts(input);
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    /// <summary>
    /// Strings with no recognizable test output pattern return (0, 0, 0).
    /// </summary>
    [Property(MaxTest = 20)]
    public Property ParseTestCounts_NoPattern_ReturnsZeros()
    {
        var gen = Gen.Elements("hello world", "foo bar baz", "just some text", "no numbers here", "[]{}()");

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var (passed, failed, skipped) = StdoutTestResultParser.ParseTestCounts(input);
            (passed + failed + skipped).Should().Be(0);
        });
    }

    /// <summary>
    /// ReDoS resistance: very long repetitive inputs complete within bounded time.
    /// </summary>
    [Fact]
    public void ParseTestCounts_LongRepetitiveInput_CompletesQuickly()
    {
        var input = string.Concat(Enumerable.Repeat("Test summary: total: ", 1000));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        StdoutTestResultParser.ParseTestCounts(input);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000,
            "parser should not catastrophically backtrack on adversarial input");
    }

    // ── IssueReferenceParser ─────────────────────────────────────────────

    /// <summary>
    /// For any arbitrary string input, ParseIssueReferences never throws.
    /// </summary>
    // TODO: Consider increasing MaxTest for crash-freedom tests — 20 iterations provides limited fuzzing coverage for random string inputs (previously 200).
    [Property(MaxTest = 20)]
    public Property ParseIssueReferences_NeverThrows_ForAnyString()
    {
        var randomStringGen = Gen.Choose(1, 200)
            .SelectMany(len => Gen.ArrayOf(Gen.Choose(0, 127).Select(i => (char)i), len))
            .Select(chars => new string(chars));

        var adversarialGen = Gen.Elements(
            "",
            "   ",
            "Closes #",
            "Fixes ##123",
            "#0",
            "#-1",
            "GH-",
            "GH-abc",
            "owner/repo#",
            "///###///",
            new string('#', 5_000),
            "Closes #99999999999999999999",
            "((([[[{{{***+++???###",
            "Fixes #1 Closes #2 Resolves #3 #4 GH-5 owner/repo#6");

        var gen = Gen.OneOf(randomStringGen, adversarialGen);

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var results = new HashSet<string>();
            IssueReferenceParser.ParseIssueReferences(input, results);
            foreach (var r in results)
            {
                r.Should().MatchRegex(@"^\d+$",
                    $"issue reference should be numeric, got '{r}' from input: [{Truncate(input)}]");
            }
        });
    }

    /// <summary>
    /// For any arbitrary string input, ParseClosingKeywords never throws.
    /// </summary>
    // TODO: Consider increasing MaxTest for crash-freedom tests — 20 iterations provides limited fuzzing coverage for random string inputs (previously 200).
    [Property(MaxTest = 20)]
    public Property ParseClosingKeywords_NeverThrows_ForAnyString()
    {
        var randomStringGen = Gen.Choose(1, 200)
            .SelectMany(len => Gen.ArrayOf(Gen.Choose(0, 127).Select(i => (char)i), len))
            .Select(chars => new string(chars));

        var adversarialGen = Gen.Elements(
            "",
            "Closes #abc",
            "Fixes##1",
            "Resolves # ",
            new string('C', 3000) + "loses #1");

        var gen = Gen.OneOf(randomStringGen, adversarialGen);

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var results = new HashSet<string>();
            IssueReferenceParser.ParseClosingKeywords(input, results);
            foreach (var r in results)
            {
                r.Should().MatchRegex(@"^\d+$",
                    $"closing keyword reference should be numeric, got '{r}'");
            }
        });
    }

    /// <summary>
    /// Null input to ParseIssueReferences does not throw.
    /// </summary>
    [Fact]
    public void ParseIssueReferences_Null_DoesNotThrow()
    {
        var results = new HashSet<string>();
        IssueReferenceParser.ParseIssueReferences(null, results);
        results.Should().BeEmpty();
    }

    /// <summary>
    /// Null input to ParseClosingKeywords does not throw.
    /// </summary>
    [Fact]
    public void ParseClosingKeywords_Null_DoesNotThrow()
    {
        var results = new HashSet<string>();
        IssueReferenceParser.ParseClosingKeywords(null, results);
        results.Should().BeEmpty();
    }

    /// <summary>
    /// Idempotence: calling ParseIssueReferences twice with the same text and set
    /// produces the same set as calling once (HashSet deduplication).
    /// </summary>
    [Property(MaxTest = 20)]
    public Property ParseIssueReferences_Idempotent()
    {
        var gen = Gen.Elements(
            "Closes #1 and fixes #2",
            "#5 #10 #15",
            "GH-42 owner/repo#99",
            "No references here",
            "Fixes #1 Fixes #1 Fixes #1");

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var firstPass = new HashSet<string>();
            IssueReferenceParser.ParseIssueReferences(input, firstPass);

            var secondPass = new HashSet<string>(firstPass);
            IssueReferenceParser.ParseIssueReferences(input, secondPass);

            secondPass.Should().BeEquivalentTo(firstPass,
                "calling ParseIssueReferences twice should not add new entries");
        });
    }

    /// <summary>
    /// ReDoS resistance for IssueReferenceParser.
    /// </summary>
    [Fact]
    public void ParseIssueReferences_LongRepetitiveInput_CompletesQuickly()
    {
        var input = string.Concat(Enumerable.Repeat("Closes #", 5000));
        var results = new HashSet<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IssueReferenceParser.ParseIssueReferences(input, results);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "parser should not catastrophically backtrack on adversarial input");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string Truncate(string? input, int maxLen = 50)
    {
        if (input is null) return "<null>";
        if (input.Length <= maxLen) return input;
        return input[..maxLen] + "...";
    }
}
