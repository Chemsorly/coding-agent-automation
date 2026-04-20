using System.Reflection;
using AwesomeAssertions;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;
using KiroWebUI.Pipeline.Services;

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
        var result = PipelineFormatting.GenerateBranchName(number, title);
        result.Should().Be(expected);
    }

    [Fact]
    public void GenerateBranchName_WithEmptyTitle_OmitsSlug()
    {
        var result = PipelineFormatting.GenerateBranchName("42", "");
        result.Should().Be("feature/auto-42");
    }

    [Fact]
    public void GenerateBranchName_WithLongTitle_TruncatesToMaxLength()
    {
        var longTitle = new string('a', 200);
        var result = PipelineFormatting.GenerateBranchName("42", longTitle);
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().StartWith("feature/auto-42-");
        result.Should().NotEndWith("-");
    }

    [Fact]
    public void GenerateBranchName_WithLongTitleAndRunId_TruncatesToMaxLength()
    {
        var longTitle = new string('a', 200);
        var runId = "abcdef1234567890";
        var result = PipelineFormatting.GenerateBranchName("42", longTitle, runId);
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().StartWith("feature/auto-42-");
        result.Should().EndWith($"-{runId[..8]}");
    }

    [Fact]
    public void GenerateBranchName_TruncationDoesNotLeaveTrailingHyphen()
    {
        // Spaces become hyphens in the slug; truncation mid-slug could leave a trailing hyphen
        var title = string.Join(" ", Enumerable.Repeat("word", 50));
        var result = PipelineFormatting.GenerateBranchName("1", title);
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().NotEndWith("-");
        result.Should().NotContain("--");
    }

    [Fact]
    public void GeneratePrBody_IncludesAllSections()
    {
        var fileChanges = new List<FileChangeSummary>
        {
            new("Added", "src/NewFile.cs"),
            new("Modified", "src/Existing.cs")
        };

        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "42",
            testsPassed: 10,
            testsFailed: 2,
            testsSkipped: 1,
            coveragePercent: 87.3,
            fileChanges: fileChanges,
            issueTitle: "Add new feature",
            issueDescription: "Implement the new feature as described.",
            acceptanceCriteria: new[] { "Must compile", "Tests pass" });

        body.Should().Contain("## Issue Context");
        body.Should().Contain("Add new feature");
        body.Should().Contain("Implement the new feature as described.");
        body.Should().Contain("Must compile");
        body.Should().Contain("Tests pass");
        body.Should().Contain("## Files Changed");
        body.Should().Contain("Added");
        body.Should().Contain("src/NewFile.cs");
        body.Should().Contain("Modified");
        body.Should().Contain("src/Existing.cs");
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
        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 5,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Fix bug",
            issueDescription: "Fix the bug.",
            acceptanceCriteria: Array.Empty<string>());

        body.Should().Contain("Not available");
    }

    [Fact]
    public void GeneratePrBody_DraftPr_IncludesWarning()
    {
        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "10",
            testsPassed: 3,
            testsFailed: 5,
            testsSkipped: 0,
            coveragePercent: 40.0,
            fileChanges: new[] { new FileChangeSummary("Modified", "src/Foo.cs") },
            issueTitle: "Partial feature",
            issueDescription: "Partial implementation.",
            acceptanceCriteria: Array.Empty<string>(),
            isDraft: true);

        body.Should().Contain("draft PR");
        body.Should().Contain("incomplete");
        body.Should().Contain("Closes #10");
    }

    [Fact]
    public void GeneratePrBody_WithComments_IncludesInputCommentsSection()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "Please handle edge cases", Author = "alice", CreatedAt = new DateTime(2026, 4, 10, 14, 30, 0, DateTimeKind.Utc) },
            new() { Id = "2", Body = "Also update the docs", Author = "bob", CreatedAt = new DateTime(2026, 4, 11, 9, 0, 0, DateTimeKind.Utc) },
        };

        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "42", testsPassed: 5, testsFailed: 0, testsSkipped: 0,
            coveragePercent: 90.0, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Feature", issueDescription: "Desc",
            acceptanceCriteria: Array.Empty<string>(), isDraft: false, comments: comments);

        body.Should().Contain("## Input Comments");
        body.Should().Contain("@alice");
        body.Should().Contain("2026-04-10 14:30 UTC");
        body.Should().Contain("Please handle edge cases");
        body.Should().Contain("@bob");
        body.Should().Contain("Also update the docs");
    }

    [Fact]
    public void GeneratePrBody_WithNoComments_OmitsInputCommentsSection()
    {
        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "1", testsPassed: 1, testsFailed: 0, testsSkipped: 0,
            coveragePercent: null, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Bug", issueDescription: "Fix",
            acceptanceCriteria: Array.Empty<string>());

        body.Should().NotContain("## Input Comments");
    }

    [Fact]
    public void GeneratePrBody_ExcludesAgentAnalysisComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "Real feedback", Author = "alice", CreatedAt = DateTime.UtcNow },
            new() { Id = "2", Body = "## 🤖 Agent Analysis\n\nPlanned approach...", Author = "bot", CreatedAt = DateTime.UtcNow },
        };

        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "5", testsPassed: 1, testsFailed: 0, testsSkipped: 0,
            coveragePercent: null, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Test", issueDescription: "Desc",
            acceptanceCriteria: Array.Empty<string>(), isDraft: false, comments: comments);

        body.Should().Contain("@alice");
        body.Should().Contain("Real feedback");
        body.Should().NotContain("@bot");
        body.Should().NotContain("Agent Analysis");
    }

    [Fact]
    public void GeneratePrBody_TruncatesLongComments()
    {
        var longBody = new string('x', 300);
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = longBody, Author = "alice", CreatedAt = DateTime.UtcNow },
        };

        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "1", testsPassed: 0, testsFailed: 0, testsSkipped: 0,
            coveragePercent: null, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "T", issueDescription: "D",
            acceptanceCriteria: Array.Empty<string>(), isDraft: false, comments: comments);

        body.Should().Contain("…");
        body.Should().NotContain(longBody);
    }

    [Fact]
    public void GenerateCommitMessage_FollowsConventionalFormat()
    {
        var msg = PipelineFormatting.GenerateCommitMessage("Add login page", "15");
        msg.Should().Be("feat: Add login page (#15)\n\nAutomated implementation via pipeline");
    }

    // --- REQ-2.6: Vestigial static helpers removed ---

    [Theory]
    [InlineData("GenerateBranchName")]
    [InlineData("GeneratePrTitle")]
    [InlineData("GeneratePrBody")]
    [InlineData("GenerateCommitMessage")]
    public void GitHubRepositoryProvider_DoesNotContainStaticWrapperMethod(string methodName)
    {
        var methods = typeof(GitHubRepositoryProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        methods.Should().NotContain(m => m.Name == methodName,
            $"GitHubRepositoryProvider should not contain '{methodName}' — it was moved to PipelineFormatting (REQ-2.6)");
    }

    [Fact]
    public void GitHubRepositoryProvider_DoesNotContainNonAlphanumericPattern()
    {
        var fields = typeof(GitHubRepositoryProvider)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        fields.Should().NotContain(f => f.Name.Contains("NonAlphanumeric", StringComparison.OrdinalIgnoreCase),
            "GitHubRepositoryProvider should not contain NonAlphanumericPattern — it was a duplicate of PipelineFormatting (REQ-2.6)");
    }
}
