using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationSelectorResolver.Resolve"/>.
/// The static Resolve method is internal and is tested directly to avoid async
/// store mock overhead — the async <c>ResolveAsync</c> wrapper is a thin pass-through.
/// </summary>
public sealed class ConsolidationSelectorResolverTests
{
    // ── Helper factories ─────────────────────────────────────────────────

    private static AgentProfile MakeProfile(
        string id,
        string[] matchLabels,
        bool enabled = true,
        int priority = 0) => new()
    {
        Id = id,
        DisplayName = id,
        AgentProviderConfigId = "agent-cfg",
        MatchLabels = matchLabels,
        Enabled = enabled,
        Priority = priority
    };

    private static ProviderConfig MakeRepoConfig(string[]? requiredLabels = null) => new()
    {
        DisplayName = "test-repo",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        RequiredLabels = requiredLabels?.ToList()
    };

    private static PipelineConfiguration MakeConfig(
        string? defaultRequiredAgentLabels = null) => new()
    {
        DefaultRequiredAgentLabels = defaultRequiredAgentLabels ?? string.Empty
    };

    // ── Step 1: required labels from repoConfig ──────────────────────────

    [Fact]
    public void Resolve_RepoConfigWithRequiredLabels_MatchingProfile_ReturnProfileMatchLabels()
    {
        // Arrange
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["kiro", "dotnet"])
        };
        var repoConfig = MakeRepoConfig(["kiro"]);
        var staticConfig = MakeConfig();
        var liveConfig = MakeConfig();

        // Act
        var result = ConsolidationSelectorResolver.Resolve(repoConfig, staticConfig, liveConfig, profiles);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void Resolve_RepoConfigWithRequiredLabels_NoMatchingProfile_ReturnRequiredLabelsDirectly()
    {
        // When required labels are present but no profile matches, the labels themselves
        // are used as the selector (not the profile's MatchLabels).
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["opencode"])
        };
        var repoConfig = MakeRepoConfig(["kiro"]);
        var staticConfig = MakeConfig();
        var liveConfig = MakeConfig();

        var result = ConsolidationSelectorResolver.Resolve(repoConfig, staticConfig, liveConfig, profiles);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "kiro" });
    }

    [Fact]
    public void Resolve_StaticConfigDefaultRequiredLabels_NoRepoConfig_MatchingProfile_ReturnProfileMatchLabels()
    {
        // Step 1 via staticConfig.DefaultRequiredAgentLabels when repoConfig is null.
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["kiro", "dotnet"])
        };
        var staticConfig = MakeConfig(defaultRequiredAgentLabels: "kiro");
        var liveConfig = MakeConfig();

        var result = ConsolidationSelectorResolver.Resolve(null, staticConfig, liveConfig, profiles);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    // ── Step 2: live DefaultRequiredAgentLabels fallback ─────────────────

    [Fact]
    public void Resolve_NoRepoLabels_LiveConfigHasDefaultLabels_MatchingProfile_ReturnProfileMatchLabels()
    {
        // No repo config, no static defaults → step 2 uses liveConfig.DefaultRequiredAgentLabels.
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["kiro", "dotnet"])
        };
        var liveConfig = MakeConfig(defaultRequiredAgentLabels: "kiro");

        var result = ConsolidationSelectorResolver.Resolve(null, MakeConfig(), liveConfig, profiles);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void Resolve_NoRepoLabels_LiveConfigHasDefaultLabels_NoMatchingProfile_ReturnDefaultLabels()
    {
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["opencode"])
        };
        var liveConfig = MakeConfig(defaultRequiredAgentLabels: "kiro");

        var result = ConsolidationSelectorResolver.Resolve(null, MakeConfig(), liveConfig, profiles);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "kiro" });
    }

    [Fact]
    public void Resolve_LiveConfigHasMultipleDefaultLabels_ParsesCommaSeparated()
    {
        // Verify comma splitting with whitespace trimming.
        var profiles = new List<AgentProfile>();
        var liveConfig = MakeConfig(defaultRequiredAgentLabels: " kiro , dotnet ");

        var result = ConsolidationSelectorResolver.Resolve(null, MakeConfig(), liveConfig, profiles);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    // ── Step 3: first enabled profile fallback ───────────────────────────

    [Fact]
    public void Resolve_NoLabelsAnywhere_EnabledProfileExists_ReturnHighestPriorityProfile()
    {
        var profiles = new List<AgentProfile>
        {
            MakeProfile("low",  ["opencode"], priority: 0),
            MakeProfile("high", ["kiro"],     priority: 10)
        };

        var result = ConsolidationSelectorResolver.Resolve(null, MakeConfig(), MakeConfig(), profiles);

        result.Should().NotBeNull();
        // Should pick the highest-priority profile's MatchLabels.
        result.Should().BeEquivalentTo(new[] { "kiro" });
    }

    [Fact]
    public void Resolve_NoLabelsAnywhere_OnlyDisabledProfiles_ReturnsNull()
    {
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["kiro"], enabled: false)
        };

        var result = ConsolidationSelectorResolver.Resolve(null, MakeConfig(), MakeConfig(), profiles);

        result.Should().BeNull();
    }

    // ── Step 4: no profiles at all (startup race) ────────────────────────

    [Fact]
    public void Resolve_NoProfiles_ReturnsNull()
    {
        var result = ConsolidationSelectorResolver.Resolve(
            null, MakeConfig(), MakeConfig(), []);

        result.Should().BeNull();
    }

    // ── Edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_RepoConfigWithEmptyRequiredLabels_FallsThroughToLiveConfig()
    {
        // An explicitly empty RequiredLabels list should not match step 1.
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["kiro"])
        };
        var repoConfig = MakeRepoConfig([]);  // empty, not null
        var liveConfig = MakeConfig(defaultRequiredAgentLabels: "kiro");

        var result = ConsolidationSelectorResolver.Resolve(repoConfig, MakeConfig(), liveConfig, profiles);

        // Falls through to step 2 (live config) then matches p1.
        result.Should().BeEquivalentTo(new[] { "kiro" });
    }

    [Fact]
    public void Resolve_EqualPriorityProfiles_OrderedByIdAscending()
    {
        // When priorities are equal, the profile with the lexicographically smallest Id wins.
        var profiles = new List<AgentProfile>
        {
            MakeProfile("z-profile", ["opencode"], priority: 5),
            MakeProfile("a-profile", ["kiro"],     priority: 5)
        };

        var result = ConsolidationSelectorResolver.Resolve(
            null, MakeConfig(), MakeConfig(), profiles);

        // Smallest Id → "a-profile" → MatchLabels = ["kiro"]
        result.Should().BeEquivalentTo(new[] { "kiro" });
    }
}
