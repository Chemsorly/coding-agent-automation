using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ReviewSummaryParserTests
{
    // ─── Parse: Well-formed output ─────────────────────────────────────────────

    [Fact]
    public void Parse_WellFormedOutput_ExtractsBothSections()
    {
        var output = """
            ## Change Summary
            Added retry logic to the HTTP client with exponential backoff. Affected files: HttpClient.cs, RetryPolicy.cs.

            ## Review Verdict
            Clean implementation with one warning about missing test coverage for timeout scenarios. 0 critical, 1 warning reported.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Added retry logic");
        changeSummary.Should().Contain("HttpClient.cs");
        verdictSummary.Should().Contain("Clean implementation");
        verdictSummary.Should().Contain("1 warning");
    }

    [Fact]
    public void Parse_WellFormedOutput_TrimsWhitespace()
    {
        var output = """
            ## Change Summary

            Short summary.

            ## Review Verdict

            Short verdict.

            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Be("Short summary.");
        verdictSummary.Should().Be("Short verdict.");
    }

    // ─── Parse: Partial output (one section only) ──────────────────────────────

    [Fact]
    public void Parse_OnlyChangeSummary_ReturnsChangeSummaryWithNullVerdict()
    {
        var output = """
            ## Change Summary
            Refactored the auth middleware to use JWT validation.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Refactored the auth middleware");
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_OnlyReviewVerdict_ReturnsVerdictWithNullChangeSummary()
    {
        var output = """
            ## Review Verdict
            No issues found, implementation follows standard patterns.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().Contain("No issues found");
    }

    // ─── Parse: Malformed output (no headings) ─────────────────────────────────

    [Fact]
    public void Parse_NoHeadings_ReturnsBothNull()
    {
        var output = "This is just some random text without any expected headings.";

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_WrongHeadingLevel_ReturnsBothNull()
    {
        var output = """
            # Change Summary
            This uses H1 instead of H2.

            # Review Verdict
            This also uses H1.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    // ─── Parse: Empty/null input ───────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_ReturnsBothNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse("");

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_NullInput_ReturnsBothNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(null);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsBothNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse("   \n\t  ");

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_HeadingsWithEmptyContent_ReturnsBothNull()
    {
        var output = """
            ## Change Summary

            ## Review Verdict
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    // ─── Parse: Case insensitivity ─────────────────────────────────────────────

    [Fact]
    public void Parse_CaseInsensitiveHeadings_StillWorks()
    {
        var output = """
            ## change summary
            Lower case heading content.

            ## review verdict
            Lower case verdict.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Lower case heading content");
        verdictSummary.Should().Contain("Lower case verdict");
    }

    // ─── TruncateAtSentenceBoundary ────────────────────────────────────────────

    [Fact]
    public void TruncateAtSentenceBoundary_ShortText_ReturnsUnchanged()
    {
        var text = "This is short.";

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be("This is short.");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_ExactlyAtLimit_ReturnsUnchanged()
    {
        var text = new string('a', 500);

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be(text);
    }

    [Fact]
    public void TruncateAtSentenceBoundary_LongText_TruncatesAtSentenceBoundary()
    {
        var text = "First sentence. Second sentence. " + new string('x', 500);

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 50);

        result.Should().Be("First sentence. Second sentence...."); // Truncated after second sentence
    }

    [Fact]
    public void TruncateAtSentenceBoundary_NoSentenceBoundary_HardTruncates()
    {
        var text = new string('a', 600);

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().HaveLength(503); // 500 + "..."
        result.Should().EndWith("...");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_NullInput_ReturnsNull()
    {
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(null);

        result.Should().BeNull();
    }

    [Fact]
    public void TruncateAtSentenceBoundary_EmptyString_ReturnsEmpty()
    {
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void TruncateAtSentenceBoundary_DefaultMaxIs500()
    {
        var text = "Short sentence. " + new string('x', 600);

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text);

        result!.Length.Should().BeLessThanOrEqualTo(503); // max 500 + "..."
    }
}
