using KiroWebUI.Pipeline.Services;
using Xunit;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Unit tests for IssueDescriptionParser.
/// Requirements: 12.1, 12.2, 12.3
/// </summary>
public class IssueDescriptionParserTests
{
    private readonly IssueDescriptionParser _parser = new();

    [Fact]
    public void Parse_WithMarkdownHeadings_ExtractsRequirementsAndCriteria()
    {
        var description = """
            ## Requirements
            Implement user authentication with OAuth2.

            ## Acceptance Criteria
            - [ ] Users can log in via Google
            - [x] Users can log in via GitHub
            - [ ] Session expires after 30 minutes
            """;

        var result = _parser.Parse(description);

        Assert.Equal("Implement user authentication with OAuth2.", result.RequirementsSection);
        Assert.Equal(3, result.AcceptanceCriteria.Count);
        Assert.Equal("Users can log in via Google", result.AcceptanceCriteria[0]);
        Assert.Equal("Users can log in via GitHub", result.AcceptanceCriteria[1]);
        Assert.Equal("Session expires after 30 minutes", result.AcceptanceCriteria[2]);
    }

    [Fact]
    public void Parse_WithDescriptionHeading_ExtractsAsRequirements()
    {
        var description = """
            ## Description
            Add a data export feature for CSV and JSON formats.
            """;

        var result = _parser.Parse(description);

        Assert.Equal("Add a data export feature for CSV and JSON formats.", result.RequirementsSection);
        Assert.Empty(result.AcceptanceCriteria);
    }

    [Fact]
    public void Parse_WithCheckboxListOnly_ExtractsAsCriteria()
    {
        var description = """
            - [ ] First task to complete
            - [x] Second task already done
            - [ ] Third task pending
            """;

        var result = _parser.Parse(description);

        Assert.Equal(3, result.AcceptanceCriteria.Count);
        Assert.Equal("First task to complete", result.AcceptanceCriteria[0]);
        Assert.Equal("Second task already done", result.AcceptanceCriteria[1]);
        Assert.Equal("Third task pending", result.AcceptanceCriteria[2]);
    }

    [Fact]
    public void Parse_PlainTextNoStructure_TreatsEntireDescriptionAsRequirements()
    {
        var description = "Just a plain text description with no markdown structure at all.";

        var result = _parser.Parse(description);

        Assert.Equal(description, result.RequirementsSection);
        Assert.Empty(result.AcceptanceCriteria);
    }

    [Fact]
    public void Parse_EmptyDescription_ReturnsEmptyResult()
    {
        var result = _parser.Parse("");

        Assert.Equal(string.Empty, result.RequirementsSection);
        Assert.Empty(result.AcceptanceCriteria);
    }

    [Fact]
    public void Parse_NullDescription_ReturnsEmptyResult()
    {
        var result = _parser.Parse(null);

        Assert.Equal(string.Empty, result.RequirementsSection);
        Assert.Empty(result.AcceptanceCriteria);
    }

    [Fact]
    public void Parse_WhitespaceOnlyDescription_ReturnsEmptyResult()
    {
        var result = _parser.Parse("   \n  \t  ");

        Assert.Equal(string.Empty, result.RequirementsSection);
        Assert.Empty(result.AcceptanceCriteria);
    }

    [Fact]
    public void Parse_OnlyAcceptanceCriteriaHeading_ExtractsCriteriaWithEmptyRequirements()
    {
        var description = """
            ## Acceptance Criteria
            - [ ] Widget renders correctly
            - [ ] Widget handles empty state
            """;

        var result = _parser.Parse(description);

        Assert.Equal(string.Empty, result.RequirementsSection);
        Assert.Equal(2, result.AcceptanceCriteria.Count);
        Assert.Equal("Widget renders correctly", result.AcceptanceCriteria[0]);
        Assert.Equal("Widget handles empty state", result.AcceptanceCriteria[1]);
    }

    [Fact]
    public void Format_ProducesStructuredOutput()
    {
        var parsed = new KiroWebUI.Pipeline.Models.ParsedIssue
        {
            RequirementsSection = "Build the API endpoint",
            AcceptanceCriteria = ["Returns 200 on success", "Returns 404 on not found"]
        };

        var formatted = _parser.Format(parsed);

        Assert.Contains("## Requirements", formatted);
        Assert.Contains("Build the API endpoint", formatted);
        Assert.Contains("## Acceptance Criteria", formatted);
        Assert.Contains("1. Returns 200 on success", formatted);
        Assert.Contains("2. Returns 404 on not found", formatted);
    }

    [Fact]
    public void Format_WithEmptyCriteria_ProducesHeadersOnly()
    {
        var parsed = new KiroWebUI.Pipeline.Models.ParsedIssue
        {
            RequirementsSection = "Some requirements",
            AcceptanceCriteria = []
        };

        var formatted = _parser.Format(parsed);

        Assert.Contains("## Requirements", formatted);
        Assert.Contains("Some requirements", formatted);
        Assert.Contains("## Acceptance Criteria", formatted);
    }
}
