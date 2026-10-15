using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.UnitTests;

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
        int priority = 0,
        string? jobTemplate = null) => new()
        {
            Id = id,
            DisplayName = id,
            AgentProviderConfigId = "agent-cfg",
            MatchLabels = matchLabels,
            Enabled = enabled,
            Priority = priority,
            JobTemplate = jobTemplate
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
    public void Resolve_RepoConfigWithRequiredLabels_MatchingProfile_ReturnProfileSelector()
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

        // Assert: returns the profile's JobTemplateKey (MatchLabels, sorted) and the profile ID
        result.Should().NotBeNull();
        result!.AgentSelector.Should().Be("dotnet,kiro");
        result.ResolvedProfileId.Should().Be("p1");
    }

    [Fact]
    public void Resolve_RepoConfigWithRequiredLabels_NoMatchingProfile_ReturnRequiredLabelsDirectly()
    {
        // When required labels are present but no profile matches, the labels themselves
        // are used as the selector (not the profile's MatchLabels) and no profile ID.
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["opencode"])
        };
        var repoConfig = MakeRepoConfig(["kiro"]);
        var staticConfig = MakeConfig();
        var liveConfig = MakeConfig();

        var result = ConsolidationSelectorResolver.Resolve(repoConfig, staticConfig, liveConfig, profiles);

        result.Should().NotBeNull();
        result!.AgentSelector.Should().Be("kiro");
        result.ResolvedProfileId.Should().BeNull();
    }

    [Fact]
    public void Resolve_StaticConfigDefaultRequiredLabels_NoRepoConfig_MatchingProfile_ReturnProfileSelector()
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
        result!.AgentSelector.Should().Be("dotnet,kiro");
        result.ResolvedProfileId.Should().Be("p1");
    }

    // ── Step 1 with JobTemplate ──────────────────────────────────────────

    [Fact]
    public void Resolve_MatchingProfile_WithJobTemplate_ReturnsJobTemplateKey()
    {
        // A profile with JobTemplate set returns the template key, not MatchLabels key.
        var profiles = new List<AgentProfile>
        {
            MakeProfile("p1", ["kiro", "dotnet", "sonnet"], jobTemplate: "kiro,dotnet")
        };
        var repoConfig = MakeRepoConfig(["kiro"]);

        var result = ConsolidationSelectorResolver.Resolve(repoConfig, MakeConfig(), MakeConfig(), profiles);

        result.Should().NotBeNull();
        // JobTemplateKey() of "kiro,dotnet" → ordinal-sorted: "dotnet,kiro"
        result!.AgentSelector.Should().Be("dotnet,kiro");
        result.ResolvedProfileId.Should().Be("p1");
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
        result!.AgentSelector.Should().Be("dotnet,kiro");
        result.ResolvedProfileId.Should().Be("p1");
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
        result!.AgentSelector.Should().Be("kiro");
        result.ResolvedProfileId.Should().BeNull();
    }

    [Fact]
    public void Resolve_LiveConfigHasMultipleDefaultLabels_ParsesCommaSeparated()
    {
        // Verify comma splitting with whitespace trimming and no profile match → labels-only result.
        var profiles = new List<AgentProfile>();
        var liveConfig = MakeConfig(defaultRequiredAgentLabels: " kiro , dotnet ");

        var result = ConsolidationSelectorResolver.Resolve(null, MakeConfig(), liveConfig, profiles);

        result.Should().NotBeNull();
        result!.AgentSelector.Should().Be("dotnet,kiro");
        result.ResolvedProfileId.Should().BeNull();
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
        // Should pick the highest-priority profile's selector and its ID.
        result!.AgentSelector.Should().Be("kiro");
        result.ResolvedProfileId.Should().Be("high");
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
        result.Should().NotBeNull();
        result!.AgentSelector.Should().Be("kiro");
        result.ResolvedProfileId.Should().Be("p1");
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
        result.Should().NotBeNull();
        result!.AgentSelector.Should().Be("kiro");
        result.ResolvedProfileId.Should().Be("a-profile");
    }

    // ── Step 3: dispatchableSelectors filter ─────────────────────────────

    [Fact]
    public void Resolve_Step3_WithDispatchableSelectors_PicksHighestPriorityProfileWithTemplate()
    {
        // Profiles: kiro,dotnet,dotnet10 (priority 0) and kiro,python,python312 (priority 10).
        // Templates exist for: dotnet,dotnet10,kiro | claude,dotnet,dotnet10 | dotnet,dotnet10,opencode
        // No default labels configured.
        // Expected: resolver returns kiro-dotnet profile → selector "dotnet,dotnet10,kiro",
        // even though kiro-python has higher numeric priority.
        // TODO [WARNING]: Both profiles here use JobTemplate = null, so JobTemplateKey() always falls back to
        // AgentSelectorKey.From(MatchLabels). When AgentProfile.JobTemplate is non-null, JobTemplateKey()
        // returns a key derived from that field ("dotnet,kiro" for jobTemplate:"kiro,dotnet") rather than from
        // MatchLabels. The dispatchableSelectors.Contains(p.JobTemplateKey()) check then uses the override key
        // instead of the MatchLabels key, which can cause a profile to match or miss a template entry in
        // unexpected ways. This interaction between the JobTemplate override field and dispatchableSelectors is
        // untested. Consider adding a test: profile with matchLabels=["kiro","dotnet","dotnet10"] and
        // jobTemplate="kiro,dotnet" → JobTemplateKey()="dotnet,kiro"; verify the filter uses "dotnet,kiro",
        // not "dotnet,dotnet10,kiro".
        var profiles = new List<AgentProfile>
        {
            MakeProfile("kiro-dotnet", ["kiro", "dotnet", "dotnet10"], priority: 0),
            MakeProfile("kiro-python", ["kiro", "python", "python312"], priority: 10),
        };
        var dispatchableSelectors = new HashSet<string>(
            ["dotnet,dotnet10,kiro", "claude,dotnet,dotnet10", "dotnet,dotnet10,opencode"],
            StringComparer.Ordinal);

        var result = ConsolidationSelectorResolver.Resolve(
            null, MakeConfig(), MakeConfig(), profiles, dispatchableSelectors);

        result.Should().NotBeNull();
        result!.AgentSelector.Should().Be("dotnet,dotnet10,kiro");
        result.ResolvedProfileId.Should().Be("kiro-dotnet");
    }

    [Fact]
    public void Resolve_Step3_WithDispatchableSelectors_NoTemplateForAnyEnabledProfile_ReturnsNull()
    {
        // The only enabled profile has no matching job template → resolver returns null and logs Warning.
        // TODO [WARNING]: This test only asserts the null return value. The Log.Warning(...) call in the
        // withTemplate.Count == 0 branch is the only operational signal telling operators why a consolidation
        // run was rejected. A future refactor that removes that log call would leave this test passing silently.
        // Consider capturing the Serilog static logger via a test sink (see LifecycleCaptureSink pattern used
        // in PipelineRunLifecycleServiceLoggingTests) and asserting that a Warning was emitted with the
        // expected profile name(s).
        var profiles = new List<AgentProfile>
        {
            MakeProfile("kiro-python", ["kiro", "python", "python312"], priority: 10),
        };
        var dispatchableSelectors = new HashSet<string>(
            ["dotnet,dotnet10,kiro", "dotnet,dotnet10,opencode"],
            StringComparer.Ordinal);

        var result = ConsolidationSelectorResolver.Resolve(
            null, MakeConfig(), MakeConfig(), profiles, dispatchableSelectors);

        result.Should().BeNull();
    }
}
