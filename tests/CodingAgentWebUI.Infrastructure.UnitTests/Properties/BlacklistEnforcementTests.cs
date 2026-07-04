using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
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
    public void PipelineConfiguration_DefaultBlacklistedPaths_ContainsAgentAndBrain()
    {
        var config = new PipelineConfiguration();
        config.BlacklistedPaths.Should().Contain(".agent");
        config.BlacklistedPaths.Should().Contain(".brain");
        config.BlacklistedPaths.Should().HaveCount(2);
    }

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

    // --- Blacklist path matching logic (tested via PathBlacklistHelper.IsPathBlacklisted) ---

    [Theory]
    [InlineData(".github/workflows/ci.yml", ".github", true)]
    [InlineData(".github/CODEOWNERS", ".github", true)]
    [InlineData(".agent/steering/rule.md", ".agent", true)]
    [InlineData(".agent/settings/mcp.json", ".agent", true)]
    [InlineData("src/Program.cs", ".agent", false)]
    [InlineData("src/Program.cs", ".github", false)]
    [InlineData(".githubignore", ".github", false)]  // Not a prefix match — no slash
    [InlineData(".agent-notes.md", ".agent", false)]   // Not a prefix match — no slash
    [InlineData(".github", ".github", true)]          // Exact match
    [InlineData(".agent", ".agent", true)]              // Exact match
    public void IsPathBlacklisted_MatchesPrefixCorrectly(string filePath, string prefix, bool expected)
    {
        var result = PathBlacklistHelper.IsPathBlacklisted(filePath, new[] { prefix });
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(".GitHub/workflows/ci.yml", ".github")]
    [InlineData(".AGENT/settings/mcp.json", ".agent")]
    [InlineData(".Agent/Steering/Rule.md", ".agent")]
    public void IsPathBlacklisted_IsCaseInsensitive(string filePath, string prefix)
    {
        PathBlacklistHelper.IsPathBlacklisted(filePath, new[] { prefix }).Should().BeTrue();
    }

    [Theory]
    [InlineData(".github\\workflows\\ci.yml", ".github")]
    [InlineData(".agent\\settings\\mcp.json", ".agent")]
    public void IsPathBlacklisted_NormalizesBackslashes(string filePath, string prefix)
    {
        PathBlacklistHelper.IsPathBlacklisted(filePath, new[] { prefix }).Should().BeTrue();
    }

    [Fact]
    public void IsPathBlacklisted_WithMultiplePrefixes_MatchesAny()
    {
        var prefixes = new[] { ".agent", ".github" };
        PathBlacklistHelper.IsPathBlacklisted(".agent/foo", prefixes).Should().BeTrue();
        PathBlacklistHelper.IsPathBlacklisted(".github/bar", prefixes).Should().BeTrue();
        PathBlacklistHelper.IsPathBlacklisted("src/main.cs", prefixes).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_WithEmptyPrefixes_ReturnsFalse()
    {
        PathBlacklistHelper.IsPathBlacklisted(".agent/foo", Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsPathBlacklisted_WithTrailingSlashOnPrefix_StillMatches()
    {
        PathBlacklistHelper.IsPathBlacklisted(".github/workflows/ci.yml", new[] { ".github/" })
            .Should().BeTrue();
    }

}
