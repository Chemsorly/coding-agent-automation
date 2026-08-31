using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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

        PipelineFormatting.IsPathBlacklisted(".github/workflows/ci.yml", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_NonMatchingPath_ReturnsFalse()
    {
        var prefixes = new List<string> { ".github", "docs" };

        PipelineFormatting.IsPathBlacklisted("src/MyService.cs", prefixes).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_CaseInsensitive_ReturnsTrue()
    {
        var prefixes = new List<string> { ".GitHub" };

        PipelineFormatting.IsPathBlacklisted(".github/workflows/ci.yml", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_BackslashNormalization_ReturnsTrue()
    {
        var prefixes = new List<string> { "src\\protected" };

        PipelineFormatting.IsPathBlacklisted("src/protected/secret.cs", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_ExactMatch_ReturnsTrue()
    {
        var prefixes = new List<string> { "README.md" };

        PipelineFormatting.IsPathBlacklisted("README.md", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_EmptyPrefixes_ReturnsFalse()
    {
        PipelineFormatting.IsPathBlacklisted("anything.cs", new List<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_PrefixWithTrailingSlash_StillMatches()
    {
        var prefixes = new List<string> { "docs/" };

        PipelineFormatting.IsPathBlacklisted("docs/readme.md", prefixes).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_PartialDirectoryName_DoesNotMatch()
    {
        // "doc" should NOT match "docs/readme.md" because it's prefix-based with "/" separator
        var prefixes = new List<string> { "doc" };

        PipelineFormatting.IsPathBlacklisted("docs/readme.md", prefixes).Should().BeFalse();
    }

    // --- GeneratePrBody ---
    // Deleted (behavior removed): GeneratePrBody_MinimalInput_ContainsRequiredSections — asserted ## Files Changed, ## Test Results, ## Coverage
    // Deleted (behavior removed): GeneratePrBody_WithFileChanges_RendersTable — asserted file table rows
    // Deleted (behavior removed): GeneratePrBody_MoreThan50Files_ShowsTruncationMessage — asserted truncation row
    // Deleted (behavior removed): GeneratePrBody_NullCoverage_ShowsNotAvailable — asserted "Not available"
    // Deleted (behavior removed): GeneratePrBody_WithCodeReview_ShowsReviewSection — asserted ## AI Code Review Findings
    // Deleted (behavior removed): GeneratePrBody_WithCodeReview_NoFindings_ShowsNoFindingsMessage — asserted "Code review: no findings"

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
    public void GeneratePrBody_WithComments_ShowsInputCommentsSection()
    {
        var comments = new List<IssueComment>
        {
            new()
            {
                Id = "1",
                Body = "Please also handle edge case X",
                Author = "reviewer",
                CreatedAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc)
            }
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#1",
                IssueTitle = "Test",
                Comments = comments,
            });

        result.Should().Contain("## Input Comments");
        result.Should().Contain("@reviewer");
        result.Should().Contain("Please also handle edge case X");
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

    [Fact]
    public void FormatQualityGateSummary_WithCoverage_IncludesCoverageDetails()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" },
            Coverage = new GateResult { GateName = "Coverage", Passed = false, Details = "26.7% below threshold 40.0%" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("Coverage ❌ (26.7% below threshold 40.0%)");
    }

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

    [Fact]
    public void FormatQualityGateSummary_WithSecurityScan_IncludesSecurityStatus()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" },
            SecurityScan = new GateResult { GateName = "Security", Passed = false, Details = "2 vulnerabilities" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("Security ❌");
    }

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
}
