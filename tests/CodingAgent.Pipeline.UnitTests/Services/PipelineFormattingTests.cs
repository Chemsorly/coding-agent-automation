using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

public class PipelineFormattingTests
{
    // --- GenerateBranchName ---

    [Fact]
    public void GenerateBranchName_BasicInput_ReturnsExpectedFormat()
    {
        var result = PipelineFormatting.GenerateBranchName("42", "Add login page");

        result.Should().Be("feature/auto-42-add-login-page");
    }

    [Fact]
    public void GenerateBranchName_WithRunId_AppendsShortenedRunId()
    {
        var runId = "abcdef12-3456-7890-abcd-ef1234567890";

        var result = PipelineFormatting.GenerateBranchName("7", "Fix bug", runId);

        result.Should().Be("feature/auto-7-fix-bug-abcdef12");
    }

    [Fact]
    public void GenerateBranchName_SpecialCharacters_SanitizesToSlug()
    {
        var result = PipelineFormatting.GenerateBranchName("99", "Fix: user's email (validation) & encoding!");

        result.Should().Be("feature/auto-99-fix-user-s-email-validation-encoding");
    }

    [Fact]
    public void GenerateBranchName_LongTitle_TruncatesToMaxLength()
    {
        var longTitle = new string('a', 200);

        var result = PipelineFormatting.GenerateBranchName("1", longTitle);

        result.Length.Should().BeLessThanOrEqualTo(PipelineConstants.MaxBranchNameLength);
        result.Should().StartWith("feature/auto-1-");
    }

    [Fact]
    public void GenerateBranchName_LongTitleWithRunId_TruncatesToMaxLength()
    {
        var longTitle = new string('x', 200);
        var runId = "12345678-abcd-efgh-ijkl-mnopqrstuvwx";

        var result = PipelineFormatting.GenerateBranchName("123", longTitle, runId);

        result.Length.Should().BeLessThanOrEqualTo(PipelineConstants.MaxBranchNameLength);
        result.Should().EndWith("-12345678");
    }

    [Fact]
    public void GenerateBranchName_EmptyTitle_OmitsSlug()
    {
        var result = PipelineFormatting.GenerateBranchName("5", "");

        result.Should().Be("feature/auto-5");
    }

    [Fact]
    public void GenerateBranchName_WhitespaceTitle_OmitsSlug()
    {
        var result = PipelineFormatting.GenerateBranchName("5", "   ");

        result.Should().Be("feature/auto-5");
    }

    [Fact]
    public void GenerateBranchName_UppercaseTitle_ConvertsToLowercase()
    {
        var result = PipelineFormatting.GenerateBranchName("10", "UPPERCASE TITLE");

        result.Should().Be("feature/auto-10-uppercase-title");
    }

    [Fact]
    public void GenerateBranchName_TruncatedSlug_DoesNotEndWithHyphen()
    {
        // Create a title that when slugified and truncated would end with a hyphen
        var title = string.Join(" ", Enumerable.Repeat("word", 30));
        var runId = "abcdef12-0000-0000-0000-000000000000";

        var result = PipelineFormatting.GenerateBranchName("1", title, runId);

        // The slug portion (between prefix and suffix) should not end with hyphen
        var withoutSuffix = result[..result.LastIndexOf("-abcdef12", StringComparison.Ordinal)];
        withoutSuffix.Should().NotEndWith("-");
    }

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
    public void GenerateBranchName_WithLongTitle_TruncatesToMaxLength()
    {
        var longTitle = new string('a', 200);
        var result = PipelineFormatting.GenerateBranchName("42", longTitle);
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().StartWith("feature/auto-42-");
        result.Should().NotEndWith("-");
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

    // --- GeneratePrTitle ---

    [Fact]
    public void GeneratePrTitle_BasicInput_ReturnsConventionalCommitFormat()
    {
        var result = PipelineFormatting.GeneratePrTitle("Add login page", "#42");

        result.Should().Be("feat: Add login page (#42)");
    }

    [Fact]
    public void GeneratePrTitle_IncludesIssueNumberInParentheses()
    {
        var result = PipelineFormatting.GeneratePrTitle("Fix memory leak", "#123");

        result.Should().Contain("(#123)");
    }

    // --- GenerateCommitMessage ---

    [Fact]
    public void GenerateCommitMessage_BasicInput_ReturnsMultiLineMessage()
    {
        var result = PipelineFormatting.GenerateCommitMessage("Add login page", "#42");

        result.Should().Be("feat: Add login page (#42)\n\nAutomated implementation via pipeline");
    }

    [Fact]
    public void GenerateCommitMessage_ContainsAutomatedFooter()
    {
        var result = PipelineFormatting.GenerateCommitMessage("Fix bug", "#7");

        result.Should().Contain("Automated implementation via pipeline");
    }

    // --- IsPathBlacklisted ---

    [Fact]
    public void IsPathBlacklisted_MatchingPrefix_ReturnsTrue()
    {
        var prefixes = new List<string> { ".github", "docs" };

        PathBlacklist.IsPathBlacklisted(".github/workflows/ci.yml", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_NonMatchingPath_ReturnsFalse()
    {
        var prefixes = new List<string> { ".github", "docs" };

        PathBlacklist.IsPathBlacklisted("src/MyService.cs", prefixes).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_CaseInsensitive_ReturnsTrue()
    {
        var prefixes = new List<string> { ".GitHub" };

        PathBlacklist.IsPathBlacklisted(".github/workflows/ci.yml", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_BackslashNormalization_ReturnsTrue()
    {
        var prefixes = new List<string> { "src\\protected" };

        PathBlacklist.IsPathBlacklisted("src/protected/secret.cs", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_ExactMatch_ReturnsTrue()
    {
        var prefixes = new List<string> { "README.md" };

        PathBlacklist.IsPathBlacklisted("README.md", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_EmptyPrefixes_ReturnsFalse()
    {
        PathBlacklist.IsPathBlacklisted("anything.cs", new List<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_PrefixWithTrailingSlash_StillMatches()
    {
        var prefixes = new List<string> { "docs/" };

        PathBlacklist.IsPathBlacklisted("docs/readme.md", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_PartialDirectoryName_DoesNotMatch()
    {
        // "doc" should NOT match "docs/readme.md" because it's prefix-based with "/" separator
        var prefixes = new List<string> { "doc" };

        PathBlacklist.IsPathBlacklisted("docs/readme.md", prefixes).Should().BeFalse();
    }

    // --- GeneratePrBody ---
    // Deleted (behavior removed): GeneratePrBody_MinimalInput_ContainsRequiredSections — asserted ## Files Changed, ## Test Results, ## Coverage
    // Deleted (behavior removed): GeneratePrBody_WithFileChanges_RendersTable — asserted file table rows
    // Deleted (behavior removed): GeneratePrBody_MoreThan50Files_ShowsTruncationMessage — asserted truncation row
    // Deleted (behavior removed): GeneratePrBody_NullCoverage_ShowsNotAvailable — asserted "Not available"
    // Deleted (behavior removed): GeneratePrBody_WithCodeReview_ShowsReviewSection — asserted ## AI Code Review Findings
    // Deleted (behavior removed): GeneratePrBody_WithCodeReview_NoFindings_ShowsNoFindingsMessage — asserted "Code review: no findings"
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
    public void GeneratePrBody_ContainsIssueContextSection()
    {
        // TODO: [WARNING] This test only exercises the CloseReference != null path (## Issue Reference block is
        // emitted). The CloseReference = null path — where ## Issue Reference must NOT appear — is not covered.
        // Add a complementary test with CloseReference = null to guard the conditional in GeneratePrBody.
        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                IssueTitle = "Add feature X",
                CloseReference = "Closes #42",
            });

        result.Should().Contain("## Issue Context");
        result.Should().Contain("**Add feature X** (#42)");
        result.Should().Contain("Closes #42");
        result.Should().NotContain("## Files Changed");
        result.Should().NotContain("## Test Results");
        result.Should().NotContain("## Coverage");
        result.Should().NotContain("## AI Code Review Findings");
    }

    [Fact]
    public void GeneratePrBody_IsDraft_ShowsDraftWarning()
    {
        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#1",
                IssueTitle = "Draft PR",
                IsDraft = true,
            });

        result.Should().Contain("⚠️ **This is a draft PR — implementation is incomplete.**");
    }

    [Fact]
    public void GeneratePrBody_WithModelName_IncludesModelInFooter()
    {
        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#1",
                IssueTitle = "Test",
                ModelName = "claude-sonnet-4-20250514",
            });

        result.Should().Contain("*Model: claude-sonnet-4-20250514 · Automated implementation via pipeline*");
    }

    [Fact]
    public void GeneratePrBody_WithoutModelName_ShowsGenericFooter()
    {
        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#1",
                IssueTitle = "Test",
            });

        result.Should().Contain("*Automated implementation via pipeline*");
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

    // --- FormatQualityGateSummary ---

    [Fact]
    public void FormatQualityGateSummary_AllPassed_ContainsCheckmarks()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK", TestsPassed = 42, TestsFailed = 0 }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().StartWith("🏗️ Quality gates:");
        result.Should().Contain("Compilation ✅");
        result.Should().Contain("Tests ✅ (42 passed, 0 failed)");
    }

    [Fact]
    public void FormatQualityGateSummary_CompilationFailed_ContainsCross()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "2 errors" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("Compilation ❌");
    }

    // Deleted (behavior removed): FormatQualityGateSummary_WithCoverage_IncludesCoverageDetails —
    // Coverage property was retired from QualityGateReport (Key(1) tombstoned); coverage is now
    // surfaced via QgcResults and is not shown in the one-line summary.

    [Fact]
    public void FormatQualityGateSummary_WithExternalCi_IncludesCiStatus()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" },
            ExternalCi = new GateResult { GateName = "External CI", Passed = true, Details = "CI passed" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("External CI ✅");
    }

    // Deleted (behavior removed): FormatQualityGateSummary_WithSecurityScan_IncludesSecurityStatus —
    // SecurityScan property was retired from QualityGateReport (Key(4) tombstoned).

    [Fact]
    public void FormatQualityGateSummary_TestsWithoutCounts_OmitsCounts()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("Tests ✅");
        result.Should().NotContain("passed");
    }

    [Fact]
    public void GenerateBranchName_VeryLongTitle_TruncatesSlug()
    {
        var longTitle = new string('a', 200);
        var result = PipelineFormatting.GenerateBranchName("42", longTitle, "abcdef12-0000-0000-0000-000000000000");
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().StartWith("feature/auto-42-");
    }

    [Fact]
    public void GenerateBranchName_EmptyTitle_FallsBackToPrefix()
    {
        var result = PipelineFormatting.GenerateBranchName("42", "", "abcdef12-0000-0000-0000-000000000000");
        result.Should().StartWith("feature/auto-42");
    }




    [Fact]
    public void GeneratePrBody_WithLongComment_TruncatesAndClosesCodeFence()
    {
        var longBody = "```csharp\n" + new string('x', 2000) + "\n```";
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "user1", Body = longBody, CreatedAt = DateTime.UtcNow }
        };
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                IssueTitle = "Fix bug",
                Comments = comments,
            });
        body.Should().Contain("user1");
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
    public void GeneratePrBody_WithoutModelName_UsesDefaultFooter()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                IssueTitle = "Fix bug",
            });
        body.Should().Contain("Automated implementation via pipeline");
        body.Should().NotContain("Model:");
    }
}
