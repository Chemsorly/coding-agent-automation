using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class ReviewSummaryParserTests
{
    [Fact]
    public void Parse_WellFormedOutput_ReturnsBothSections()
    {
        var output = """
            ## Change Summary
            Added pagination support to the /users endpoint. Modified UserRepository and UserController to accept page/pageSize parameters.

            ## Review Verdict
            Clean implementation with 1 warning about missing index on the new offset query. No security or correctness issues found.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().NotBeNull();
        changeSummary.Should().Contain("pagination support");
        verdictSummary.Should().NotBeNull();
        verdictSummary.Should().Contain("missing index");
    }

    [Fact]
    public void Parse_OnlyChangeSummary_ReturnsPartialResult()
    {
        var output = """
            ## Change Summary
            Refactored the auth middleware to support JWT refresh tokens.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().NotBeNull();
        changeSummary.Should().Contain("JWT refresh tokens");
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_OnlyReviewVerdict_ReturnsPartialResult()
    {
        var output = """
            ## Review Verdict
            No issues found. Implementation follows standard patterns.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().NotBeNull();
        verdictSummary.Should().Contain("No issues found");
    }

    [Fact]
    public void Parse_MalformedOutput_NoHeadings_ReturnsNullNull()
    {
        var output = "This is just some random text without any headings.";

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNullNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(string.Empty);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_NullInput_ReturnsNullNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(null);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsNullNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse("   \n  \n  ");

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_HeadingsWithEmptyContent_ReturnsNullNull()
    {
        var output = """
            ## Change Summary

            ## Review Verdict

            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_CaseInsensitiveHeadings_StillMatches()
    {
        var output = """
            ## change summary
            Some change description here.

            ## review verdict
            Looks good overall.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().NotBeNull();
        changeSummary.Should().Contain("change description");
        verdictSummary.Should().NotBeNull();
        verdictSummary.Should().Contain("Looks good");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_ShortText_ReturnsUnchanged()
    {
        var text = "Short text.";
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);
        result.Should().Be("Short text.");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_ExactlyAtLimit_ReturnsUnchanged()
    {
        var text = new string('a', 500);
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);
        result.Should().Be(text);
    }

    [Fact]
    public void TruncateAtSentenceBoundary_LongText_TruncatesAtSentence()
    {
        var text = "First sentence. Second sentence. " + new string('x', 500);
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 50);

        result.Should().EndWith("...");
        result.Should().Contain("First sentence.");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_LongTextNoSentenceBoundary_HardTruncates()
    {
        var text = new string('a', 600);
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().HaveLength(503); // 500 + "..."
        result.Should().EndWith("...");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_Null_ReturnsNull()
    {
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(null);
        result.Should().BeNull();
    }
}
