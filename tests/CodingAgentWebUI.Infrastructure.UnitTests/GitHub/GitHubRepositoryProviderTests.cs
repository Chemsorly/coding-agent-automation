using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

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

    // Deleted (behavior removed): GeneratePrBody_IncludesAllSections — asserted ## Files Changed, ## Test Results, ## Coverage
    // Deleted (behavior removed): GeneratePrBody_WithNullCoverage_ShowsNotAvailable — asserted "Not available" from ## Coverage
    // Deleted (behavior removed): GeneratePrBody_CodeReviewDisabled_OmitsSection — asserted absence of AI Code Review Findings
    // Deleted (behavior removed): GeneratePrBody_CodeReviewNoFindings_ShowsNoFindings — asserted code review no findings
    // Deleted (behavior removed): GeneratePrBody_CodeReviewWithFindings_ShowsAgents — asserted code review agents
    // Deleted (behavior removed): GeneratePrBody_CodeReviewWithFindings_ShowsSeverityTable — asserted severity table
    // Deleted (behavior removed): GeneratePrBody_CodeReviewWithFindings_PerAgentCollapsibleBlocks — asserted collapsible blocks
    // Deleted (behavior removed): GeneratePrBody_CodeReviewAgentFindings_TruncatedAt10000Chars — asserted findings truncation
    // Deleted (behavior removed): GeneratePrBody_CodeReviewZeroCounts_OmitsZeroRows — asserted zero-count row omission
    // Deleted (behavior removed): GeneratePrBody_CodeReviewNoAgents_OmitsAgentsLine — asserted agents line omission

    [Fact]
    public void GeneratePrBody_DraftPr_IncludesWarning()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#10",
                IssueTitle = "Partial feature",
                IsDraft = true,
                CloseReference = "Closes #10",
            });

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

        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                IssueTitle = "Feature",
                Comments = comments,
            });

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
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#1",
                IssueTitle = "Bug",
            });

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

        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "5",
                IssueTitle = "Test",
                Comments = comments,
            });

        body.Should().Contain("@alice");
        body.Should().Contain("Real feedback");
        body.Should().NotContain("@bot");
        body.Should().NotContain("Agent Analysis");
    }

    [Fact]
    public void GeneratePrBody_TruncatesLongComments()
    {
        var longBody = new string('x', 2500);
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = longBody, Author = "alice", CreatedAt = DateTime.UtcNow },
        };

        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#1",
                IssueTitle = "T",
                Comments = comments,
            });

        body.Should().Contain("…");
        body.Should().NotContain(longBody);
    }

    [Fact]
    public void GenerateCommitMessage_FollowsConventionalFormat()
    {
        var msg = PipelineFormatting.GenerateCommitMessage("Add login page", "#15");
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

    // --- Model in PR body tests ---

    [Fact]
    public void GeneratePrBody_WithModelName_IncludesModelInFooter()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                IssueTitle = "Test",
                ModelName = "claude-sonnet-4.6",
            });

        body.Should().Contain("Model: claude-sonnet-4.6");
    }

    [Fact]
    public void GeneratePrBody_WithoutModelName_UsesDefaultFooter()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                IssueTitle = "Test",
            });

        body.Should().Contain("Automated implementation via pipeline");
        body.Should().NotContain("Model:");
    }
}
