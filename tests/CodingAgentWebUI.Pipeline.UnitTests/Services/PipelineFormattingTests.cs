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
        var result = PipelineFormatting.GeneratePrTitle("Add login page", "42");

        result.Should().Be("feat: Add login page (#42)");
    }

    [Fact]
    public void GeneratePrTitle_IncludesIssueNumberInParentheses()
    {
        var result = PipelineFormatting.GeneratePrTitle("Fix memory leak", "123");

        result.Should().Contain("(#123)");
    }

    // --- GenerateCommitMessage ---

    [Fact]
    public void GenerateCommitMessage_BasicInput_ReturnsMultiLineMessage()
    {
        var result = PipelineFormatting.GenerateCommitMessage("Add login page", "42");

        result.Should().Be("feat: Add login page (#42)\n\nAutomated implementation via pipeline");
    }

    [Fact]
    public void GenerateCommitMessage_ContainsAutomatedFooter()
    {
        var result = PipelineFormatting.GenerateCommitMessage("Fix bug", "7");

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

    [Fact]
    public void GeneratePrBody_MinimalInput_ContainsRequiredSections()
    {
        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "42",
            testsPassed: 10,
            testsFailed: 0,
            testsSkipped: 2,
            coveragePercent: 85.5,
            fileChanges: [],
            issueTitle: "Add feature X");

        result.Should().Contain("## Issue Context");
        result.Should().Contain("**Add feature X** (#42)");
        result.Should().Contain("## Files Changed");
        result.Should().Contain("No file changes detected.");
        result.Should().Contain("## Test Results");
        result.Should().Contain("- Passed: 10");
        result.Should().Contain("- Failed: 0");
        result.Should().Contain("- Skipped: 2");
        result.Should().Contain("## Coverage");
        result.Should().Contain("85.5%");
        result.Should().Contain("Closes #42");
    }

    [Fact]
    public void GeneratePrBody_WithFileChanges_RendersTable()
    {
        var files = new List<FileChangeSummary>
        {
            new("Added", "src/NewFile.cs"),
            new("Modified", "src/Existing.cs", LinesAdded: 10, LinesDeleted: 3)
        };

        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 5,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: files,
            issueTitle: "Test");

        result.Should().Contain("| Status | File |");
        result.Should().Contain("| Added | `src/NewFile.cs` |");
        result.Should().Contain("| Modified | `src/Existing.cs` |");
    }

    [Fact]
    public void GeneratePrBody_MoreThan50Files_ShowsTruncationMessage()
    {
        var files = Enumerable.Range(1, 60)
            .Select(i => new FileChangeSummary("Modified", $"src/File{i}.cs"))
            .ToList();

        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: files,
            issueTitle: "Many files");

        result.Should().Contain("*(and 10 more)*");
    }

    [Fact]
    public void GeneratePrBody_NullCoverage_ShowsNotAvailable()
    {
        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test");

        result.Should().Contain("Not available");
    }

    [Fact]
    public void GeneratePrBody_IsDraft_ShowsDraftWarning()
    {
        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Draft PR",
            isDraft: true);

        result.Should().Contain("⚠️ **This is a draft PR — implementation is incomplete.**");
    }

    [Fact]
    public void GeneratePrBody_WithModelName_IncludesModelInFooter()
    {
        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test",
            modelName: "claude-sonnet-4-20250514");

        result.Should().Contain("*Model: claude-sonnet-4-20250514 · Automated implementation via pipeline*");
    }

    [Fact]
    public void GeneratePrBody_WithoutModelName_ShowsGenericFooter()
    {
        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test");

        result.Should().Contain("*Automated implementation via pipeline*");
    }

    [Fact]
    public void GeneratePrBody_WithBlacklistedFiles_ShowsWarningSection()
    {
        var blacklisted = new List<string> { ".github/workflows/ci.yml", "package-lock.json" };

        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test",
            blacklistedFilesDetected: blacklisted);

        result.Should().Contain("## ⚠️ Blacklisted Files Excluded");
        result.Should().Contain("- `.github/workflows/ci.yml`");
        result.Should().Contain("- `package-lock.json`");
    }

    [Fact]
    public void GeneratePrBody_WithCodeReview_ShowsReviewSection()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["security-agent", "style-agent"],
            CriticalCount: 1,
            WarningCount: 2,
            SuggestionCount: 3,
            AgentFindings: [new AgentFindings("security-agent", "Found SQL injection risk")]);

        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test",
            codeReviewSummary: review);

        result.Should().Contain("## AI Code Review Findings");
        result.Should().Contain("security-agent, style-agent");
        result.Should().Contain("CRITICAL | 1 | Fixed");
        result.Should().Contain("WARNING | 2 | Reported (TODO comments added)");
        result.Should().Contain("SUGGESTION | 3 | Reported only");
        result.Should().Contain("Found SQL injection risk");
    }

    [Fact]
    public void GeneratePrBody_WithCodeReview_NoFindings_ShowsNoFindingsMessage()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["agent-1"],
            CriticalCount: 0,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: []);

        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test",
            codeReviewSummary: review);

        result.Should().Contain("Code review: no findings");
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

        var result = PipelineFormatting.GeneratePrBody(
            issueNumber: "1",
            testsPassed: 0,
            testsFailed: 0,
            testsSkipped: 0,
            coveragePercent: null,
            fileChanges: [],
            issueTitle: "Test",
            comments: comments);

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
