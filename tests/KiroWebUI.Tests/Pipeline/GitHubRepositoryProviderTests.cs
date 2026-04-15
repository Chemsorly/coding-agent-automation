using AwesomeAssertions;
using KiroWebUI.Pipeline.Providers;

namespace KiroWebUI.Tests.Pipeline;

public class GitHubRepositoryProviderTests
{
    [Theory]
    [InlineData("Fix the bug!", "42", "feature/auto-42-fix-the-bug")]
    [InlineData("Hello World", "1", "feature/auto-1-hello-world")]
    [InlineData("UPPER CASE", "99", "feature/auto-99-upper-case")]
    [InlineData("special @#$ chars", "5", "feature/auto-5-special-chars")]
    [InlineData("---leading-trailing---", "7", "feature/auto-7-leading-trailing")]
    [InlineData("multiple   spaces", "3", "feature/auto-3-multiple-spaces")]
    public void GenerateBranchName_WithSpecialCharacters_ProducesValidSlug(string title, string number, string expected)
    {
        var result = GitHubRepositoryProvider.GenerateBranchName(number, title);
        result.Should().Be(expected);
    }

    [Fact]
    public void GenerateBranchName_WithEmptyTitle_OmitsSlug()
    {
        var result = GitHubRepositoryProvider.GenerateBranchName("42", "");
        result.Should().Be("feature/auto-42");
    }

    [Fact]
    public void GeneratePrBody_IncludesAllSections()
    {
        var body = GitHubRepositoryProvider.GeneratePrBody(
            issueNumber: "42",
            testsPassed: 10,
            testsFailed: 2,
            testsSkipped: 1,
            coveragePercent: 87.3,
            implementationSummary: "Added new feature");

        body.Should().Contain("## Implementation Summary");
        body.Should().Contain("Added new feature");
        body.Should().Contain("## Test Results");
        body.Should().Contain("Passed: 10");
        body.Should().Contain("Failed: 2");
        body.Should().Contain("Skipped: 1");
        body.Should().Contain("## Coverage");
        body.Should().Contain("87.3%");
        body.Should().Contain("Closes #42");
    }

    [Fact]
    public void GeneratePrBody_WithNullCoverage_ShowsNotAvailable()
    {
        var body = GitHubRepositoryProvider.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 5,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            implementationSummary: "Summary");

        body.Should().Contain("Not available");
    }

    [Fact]
    public void GeneratePrBody_DraftPr_IncludesWarning()
    {
        var body = GitHubRepositoryProvider.GeneratePrBody(
            issueNumber: "10",
            testsPassed: 3,
            testsFailed: 5,
            testsSkipped: 0,
            coveragePercent: 40.0,
            implementationSummary: "Partial implementation",
            isDraft: true);

        body.Should().Contain("draft PR");
        body.Should().Contain("incomplete");
        body.Should().Contain("Closes #10");
    }

    [Fact]
    public void GenerateCommitMessage_FollowsConventionalFormat()
    {
        var msg = GitHubRepositoryProvider.GenerateCommitMessage("Add login page", "15");
        msg.Should().Be("feat: Add login page (#15)\n\nAutomated implementation via pipeline");
    }
}
