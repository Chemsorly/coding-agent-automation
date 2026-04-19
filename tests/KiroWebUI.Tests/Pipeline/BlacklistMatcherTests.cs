using KiroWebUI.Pipeline.Services;
using Xunit;

namespace KiroWebUI.Tests.Pipeline;

public class BlacklistMatcherTests
{
    private static readonly IReadOnlyList<string> DefaultBlacklist = new[] { ".kiro", ".github" };

    [Theory]
    [InlineData(".kiro/settings/mcp.json")]
    [InlineData(".kiro/steering/foo.md")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/CODEOWNERS")]
    [InlineData(".KIRO/Settings/Mcp.json")]
    [InlineData(".GitHub/Workflows/CI.yml")]
    public void IsBlacklisted_MatchesPrefixCaseInsensitively(string path)
    {
        Assert.True(BlacklistMatcher.IsBlacklisted(path, DefaultBlacklist));
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData(".gitignore")]
    [InlineData(".kirox/something")]
    [InlineData(".github-notes.md")]
    public void IsBlacklisted_DoesNotMatchNonBlacklistedPaths(string path)
    {
        Assert.False(BlacklistMatcher.IsBlacklisted(path, DefaultBlacklist));
    }

    [Fact]
    public void IsBlacklisted_EmptyBlacklist_ReturnsFalse()
    {
        Assert.False(BlacklistMatcher.IsBlacklisted(".kiro/foo", Array.Empty<string>()));
    }

    [Fact]
    public void IsBlacklisted_ExactMatch_ReturnsTrue()
    {
        Assert.True(BlacklistMatcher.IsBlacklisted(".kiro", DefaultBlacklist));
    }

    [Fact]
    public void IsBlacklisted_BackslashNormalization()
    {
        Assert.True(BlacklistMatcher.IsBlacklisted(".kiro\\settings\\mcp.json", DefaultBlacklist));
    }

    [Fact]
    public void IsBlacklisted_PrefixWithTrailingSlash()
    {
        var blacklist = new[] { ".kiro/" };
        Assert.True(BlacklistMatcher.IsBlacklisted(".kiro/foo.md", blacklist));
    }

    [Fact]
    public void FindBlacklistedPaths_ReturnsOnlyMatches()
    {
        var paths = new[] { "src/Foo.cs", ".kiro/settings.json", ".github/ci.yml", "README.md" };
        var result = BlacklistMatcher.FindBlacklistedPaths(paths, DefaultBlacklist);
        Assert.Equal(2, result.Count);
        Assert.Contains(".kiro/settings.json", result);
        Assert.Contains(".github/ci.yml", result);
    }

    [Fact]
    public void FindBlacklistedPaths_EmptyBlacklist_ReturnsEmpty()
    {
        var paths = new[] { ".kiro/foo", ".github/bar" };
        var result = BlacklistMatcher.FindBlacklistedPaths(paths, Array.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void FindBlacklistedPaths_NoPaths_ReturnsEmpty()
    {
        var result = BlacklistMatcher.FindBlacklistedPaths(Array.Empty<string>(), DefaultBlacklist);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("src/../.kiro/settings.json")]
    [InlineData("foo/../.github/workflows/ci.yml")]
    public void IsBlacklisted_PathTraversal_StillMatches(string path)
    {
        Assert.True(BlacklistMatcher.IsBlacklisted(path, DefaultBlacklist));
    }

    [Fact]
    public void IsBlacklisted_DotSegment_Normalized()
    {
        Assert.True(BlacklistMatcher.IsBlacklisted("./.kiro/foo", DefaultBlacklist));
    }
}
