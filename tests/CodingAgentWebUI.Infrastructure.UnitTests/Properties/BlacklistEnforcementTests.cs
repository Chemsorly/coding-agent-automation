using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Tests for blacklisted path enforcement logic (GIT-04).
/// Covers prefix matching, case insensitivity, path normalization,
/// PR body generation with blacklisted files, and configuration defaults.
/// </summary>
public class BlacklistEnforcementTests
{
    // --- PipelineConfiguration defaults ---

    [Fact]
    public void PipelineConfiguration_DefaultBlacklistedPaths_ContainsKiroAndGitHub()
    {
        var config = new PipelineConfiguration();
        config.BlacklistedPaths.Should().Contain(".kiro");
        config.BlacklistedPaths.Should().Contain(".github");
        config.BlacklistedPaths.Should().Contain(".brain");
        config.BlacklistedPaths.Should().HaveCount(3);
    }

    [Fact]
    public void PipelineConfiguration_DefaultBlacklistMode_IsWarnAndExclude()
    {
        var config = new PipelineConfiguration();
        config.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
    }

    // --- PipelineRun.BlacklistedFilesDetected ---

    [Fact]
    public void PipelineRun_BlacklistedFilesDetected_DefaultsToEmpty()
    {
        var run = new PipelineRun
        {
            RunId = "test",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.BlacklistedFilesDetected.Should().BeEmpty();
    }

    // --- Blacklist path matching logic (tested via PipelineFormatting.IsPathBlacklisted) ---

    [Theory]
    [InlineData(".github/workflows/ci.yml", ".github", true)]
    [InlineData(".github/CODEOWNERS", ".github", true)]
    [InlineData(".kiro/steering/rule.md", ".kiro", true)]
    [InlineData(".kiro/settings/mcp.json", ".kiro", true)]
    [InlineData("src/Program.cs", ".kiro", false)]
    [InlineData("src/Program.cs", ".github", false)]
    [InlineData(".githubignore", ".github", false)]  // Not a prefix match — no slash
    [InlineData(".kiro-notes.md", ".kiro", false)]   // Not a prefix match — no slash
    [InlineData(".github", ".github", true)]          // Exact match
    [InlineData(".kiro", ".kiro", true)]              // Exact match
    public void IsPathBlacklisted_MatchesPrefixCorrectly(string filePath, string prefix, bool expected)
    {
        var result = PipelineFormatting.IsPathBlacklisted(filePath, new[] { prefix });
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(".GitHub/workflows/ci.yml", ".github")]
    [InlineData(".KIRO/settings/mcp.json", ".kiro")]
    [InlineData(".Kiro/Steering/Rule.md", ".kiro")]
    public void IsPathBlacklisted_IsCaseInsensitive(string filePath, string prefix)
    {
        PipelineFormatting.IsPathBlacklisted(filePath, new[] { prefix }).Should().BeTrue();
    }

    [Theory]
    [InlineData(".github\\workflows\\ci.yml", ".github")]
    [InlineData(".kiro\\settings\\mcp.json", ".kiro")]
    public void IsPathBlacklisted_NormalizesBackslashes(string filePath, string prefix)
    {
        PipelineFormatting.IsPathBlacklisted(filePath, new[] { prefix }).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_WithMultiplePrefixes_MatchesAny()
    {
        var prefixes = new[] { ".kiro", ".github" };
        PipelineFormatting.IsPathBlacklisted(".kiro/foo", prefixes).Should().BeTrue();
        PipelineFormatting.IsPathBlacklisted(".github/bar", prefixes).Should().BeTrue();
        PipelineFormatting.IsPathBlacklisted("src/main.cs", prefixes).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_WithEmptyPrefixes_ReturnsFalse()
    {
        PipelineFormatting.IsPathBlacklisted(".kiro/foo", Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_WithTrailingSlashOnPrefix_StillMatches()
    {
        PipelineFormatting.IsPathBlacklisted(".github/workflows/ci.yml", new[] { ".github/" })
            .Should().BeTrue();
    }

    // --- PR body with blacklisted files ---

    [Fact]
    public void GeneratePrBody_WithBlacklistedFiles_IncludesWarningSection()
    {
        var blacklisted = new[] { ".kiro/steering/rule.md", ".github/workflows/ci.yml" };

        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "42", testsPassed: 5, testsFailed: 0, testsSkipped: 0,
            coveragePercent: 90.0, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Feature",
            blacklistedFilesDetected: blacklisted);

        body.Should().Contain("## ⚠️ Blacklisted Files Excluded");
        body.Should().Contain(".kiro/steering/rule.md");
        body.Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public void GeneratePrBody_WithNoBlacklistedFiles_OmitsWarningSection()
    {
        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "1", testsPassed: 1, testsFailed: 0, testsSkipped: 0,
            coveragePercent: null, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Bug");

        body.Should().NotContain("Blacklisted Files Excluded");
    }

    [Fact]
    public void GeneratePrBody_WithNullBlacklistedFiles_OmitsWarningSection()
    {
        var body = PipelineFormatting.GeneratePrBody(
            issueNumber: "1", testsPassed: 1, testsFailed: 0, testsSkipped: 0,
            coveragePercent: null, fileChanges: Array.Empty<FileChangeSummary>(),
            issueTitle: "Bug",
            blacklistedFilesDetected: null);

        body.Should().NotContain("Blacklisted Files Excluded");
    }

    // --- BlacklistMode enum ---

    [Fact]
    public void BlacklistMode_HasExpectedValues()
    {
        Enum.GetValues<BlacklistMode>().Should().HaveCount(2);
        Enum.IsDefined(BlacklistMode.WarnAndExclude).Should().BeTrue();
        Enum.IsDefined(BlacklistMode.Fail).Should().BeTrue();
    }
}
