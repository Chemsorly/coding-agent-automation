using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="ProviderConfigExtensions"/>.
/// Verifies lookup, null-return, and throw-on-missing behavior for both extension methods.
/// </summary>
public class ProviderConfigExtensionsTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static ProviderConfig MakeConfig(string id) => new()
    {
        Id = id,
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = $"Config {id}",
        Settings = new Dictionary<string, string>()
    };

    // ── TryGetProviderConfig ─────────────────────────────────────────────

    [Fact]
    public void TryGetProviderConfig_WhenIdMatches_ReturnsConfig()
    {
        var configs = new List<ProviderConfig> { MakeConfig("abc"), MakeConfig("def") };

        var result = configs.TryGetProviderConfig("abc");

        result.Should().NotBeNull();
        result!.Id.Should().Be("abc");
    }

    [Fact]
    public void TryGetProviderConfig_WhenIdNotFound_ReturnsNull()
    {
        var configs = new List<ProviderConfig> { MakeConfig("abc") };

        var result = configs.TryGetProviderConfig("xyz");

        result.Should().BeNull();
    }

    [Fact]
    public void TryGetProviderConfig_WithEmptyList_ReturnsNull()
    {
        var configs = new List<ProviderConfig>();

        var result = configs.TryGetProviderConfig("abc");

        result.Should().BeNull();
    }

    [Fact]
    public void TryGetProviderConfig_WhenMultipleConfigs_ReturnsCorrectOne()
    {
        var configs = new List<ProviderConfig>
        {
            MakeConfig("first"),
            MakeConfig("target"),
            MakeConfig("last")
        };

        var result = configs.TryGetProviderConfig("target");

        result.Should().NotBeNull();
        result!.Id.Should().Be("target");
    }

    // ── GetRequiredProviderConfig ────────────────────────────────────────

    [Fact]
    public void GetRequiredProviderConfig_WhenIdMatches_ReturnsConfig()
    {
        var configs = new List<ProviderConfig> { MakeConfig("abc"), MakeConfig("def") };

        var result = configs.GetRequiredProviderConfig("abc", "Repo provider config");

        result.Should().NotBeNull();
        result.Id.Should().Be("abc");
    }

    [Fact]
    public void GetRequiredProviderConfig_WhenMultipleConfigs_ReturnsMatchingOne()
    {
        var configs = new List<ProviderConfig>
        {
            MakeConfig("first"),
            MakeConfig("target"),
            MakeConfig("last")
        };

        var result = configs.GetRequiredProviderConfig("target", "Some config");

        result.Id.Should().Be("target");
    }

    [Fact]
    public void GetRequiredProviderConfig_WhenIdNotFound_ThrowsInvalidOperationException()
    {
        var configs = new List<ProviderConfig> { MakeConfig("abc") };

        var act = () => configs.GetRequiredProviderConfig("missing-id", "Repository provider config");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetRequiredProviderConfig_WithEmptyList_ThrowsInvalidOperationException()
    {
        var configs = new List<ProviderConfig>();

        var act = () => configs.GetRequiredProviderConfig("any-id", "Agent provider config");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetRequiredProviderConfig_MessageContainsConfigNameAndId()
    {
        var configs = new List<ProviderConfig> { MakeConfig("abc") };

        var act = () => configs.GetRequiredProviderConfig("non-existent-repo-config", "Repository provider config");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Repository provider config*")
            .WithMessage("*non-existent-repo-config*");
    }
}
