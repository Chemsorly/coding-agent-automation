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
            Added retry logic to the HTTP client with exponential backoff. Affected files: HttpClient.cs, RetryPolicy.cs.

            ## Review Verdict
            Clean implementation following standard patterns. One warning about missing test coverage for the backoff calculation.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("retry logic");
        verdictSummary.Should().Contain("Clean implementation");
    }

    [Fact]
    public void Parse_OnlyChangeSummary_ReturnsChangeSummaryOnly()
    {
        var output = """
            ## Change Summary
            Refactored the database connection pool to use a bounded semaphore.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().NotBeNull();
        changeSummary.Should().Contain("database connection pool");
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_OnlyReviewVerdict_ReturnsVerdictOnly()
    {
        var output = """
            ## Review Verdict
            No issues found. Implementation follows established patterns.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().NotBeNull();
        verdictSummary.Should().Contain("No issues found");
    }

    [Fact]
    public void Parse_MalformedOutput_NoHeadings_ReturnsBothNull()
    {
        var output = "This is some random text without any headings or structure.";

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

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
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse("   \n\n  \t  ");

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

    [Fact]
    public void Parse_HeadingsCaseInsensitive_ReturnsBothSections()
    {
        var output = """
            ## change summary
            Lower case heading content.

            ## review verdict
            Lower case verdict content.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Lower case heading content");
        verdictSummary.Should().Contain("Lower case verdict content");
    }

    [Fact]
    public void Parse_ExtraContentBeforeHeadings_StillParses()
    {
        var output = """
            Here is some preamble text the agent wrote.

            ## Change Summary
            The PR adds logging to the startup path.

            ## Review Verdict
            Looks good, no issues found.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("logging");
        verdictSummary.Should().Contain("no issues found");
    }
}
