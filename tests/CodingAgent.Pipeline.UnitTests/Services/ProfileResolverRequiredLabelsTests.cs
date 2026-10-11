using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ProfileResolver.ResolveByRequiredLabels"/>: the closest-fit covering profile
/// wins (fewest MatchLabels), then higher Priority, then lower Id (ordinal).
/// </summary>
public sealed class ProfileResolverRequiredLabelsTests
{
    // ── Variant profile scenario ─────────────────────────────────────────────────

    /// <summary>
    /// A plain .NET repository (3 labels) gets the 3-label profile, not the 4-label sonnet variant,
    /// because the plain profile has fewer labels while still covering all required labels.
    /// </summary>
    [Fact]
    public void VariantProfile_DotNetRepoGetsBaseProfile()
    {
        var dotnetProfile = new AgentProfile
        {
            Id = "dotnet-profile",
            DisplayName = ".NET",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };
        var sonnetProfile = new AgentProfile
        {
            Id = "sonnet-profile",
            DisplayName = ".NET Sonnet",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10", "sonnet"]
        };
        var profiles = new[] { dotnetProfile, sonnetProfile };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["kiro", "dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("dotnet-profile");
    }

    /// <summary>
    /// A sonnet-labelled repository (4 labels) gets the 4-label sonnet variant because that profile
    /// is the only one covering all 4 required labels.
    /// </summary>
    [Fact]
    public void VariantProfile_SonnetRepoGetsSonnetProfile()
    {
        var dotnetProfile = new AgentProfile
        {
            Id = "dotnet-profile",
            DisplayName = ".NET",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };
        var sonnetProfile = new AgentProfile
        {
            Id = "sonnet-profile",
            DisplayName = ".NET Sonnet",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10", "sonnet"]
        };
        var profiles = new[] { dotnetProfile, sonnetProfile };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["kiro", "dotnet", "dotnet10", "sonnet"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("sonnet-profile");
    }

    // ── Polyglot profile scenario ────────────────────────────────────────────────

    /// <summary>
    /// A pure .NET repository (3 labels) gets the 3-label .NET profile, not the 5-label polyglot.
    /// Both profiles cover the 3 required labels, but the .NET profile has fewer labels — it wins.
    /// This is the core bug fix: before the fix the polyglot (most labels) would have won.
    /// </summary>
    [Fact]
    public void PolyglotProfile_DotNetRepoGetsDotNetProfile()
    {
        var dotnetProfile = new AgentProfile
        {
            Id = "dotnet-profile",
            DisplayName = ".NET",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };
        var polyglotProfile = new AgentProfile
        {
            Id = "polyglot-profile",
            DisplayName = "Polyglot",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10", "python", "python312"]
        };
        var profiles = new[] { dotnetProfile, polyglotProfile };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["kiro", "dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("dotnet-profile");
    }

    /// <summary>
    /// A polyglot repository (5 labels) gets the 5-label polyglot profile because only that profile
    /// covers all 5 required labels (the 3-label .NET profile is missing python and python312).
    /// </summary>
    [Fact]
    public void PolyglotProfile_PolyglotRepoGetsPolyglotProfile()
    {
        var dotnetProfile = new AgentProfile
        {
            Id = "dotnet-profile",
            DisplayName = ".NET",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };
        var polyglotProfile = new AgentProfile
        {
            Id = "polyglot-profile",
            DisplayName = "Polyglot",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10", "python", "python312"]
        };
        var profiles = new[] { dotnetProfile, polyglotProfile };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["kiro", "dotnet", "dotnet10", "python", "python312"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("polyglot-profile");
    }

    // ── Tiebreakers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When two covering profiles have the same MatchLabels count, the higher Priority wins.
    /// </summary>
    [Fact]
    public void Tiebreaker_HigherPriorityWins()
    {
        var lowPriority = new AgentProfile
        {
            Id = "low",
            DisplayName = "Low Priority",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["dotnet", "dotnet10"],
            Priority = 1
        };
        var highPriority = new AgentProfile
        {
            Id = "high",
            DisplayName = "High Priority",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["dotnet", "dotnet10"],
            Priority = 10
        };
        var profiles = new[] { lowPriority, highPriority };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("high");
    }

    /// <summary>
    /// When two covering profiles have the same label count and Priority, the ordinal-lower Id wins.
    /// Uses Ids "A-profile" and "a-profile" where ordinal order differs from case-insensitive:
    /// uppercase A (65) sorts before lowercase a (97) under StringComparer.Ordinal.
    /// </summary>
    [Fact]
    public void Tiebreaker_OrdinalLowerIdWins()
    {
        var upperProfile = new AgentProfile
        {
            Id = "A-profile",
            DisplayName = "Upper",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["dotnet", "dotnet10"],
            Priority = 0
        };
        var lowerProfile = new AgentProfile
        {
            Id = "a-profile",
            DisplayName = "Lower",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["dotnet", "dotnet10"],
            Priority = 0
        };
        // Insert in reverse order to confirm ordering is not positional
        var profiles = new[] { lowerProfile, upperProfile };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        // "A-profile" < "a-profile" under StringComparer.Ordinal (A=65, a=97)
        result!.Id.Should().Be("A-profile");
    }

    // ── Disabled profile ─────────────────────────────────────────────────────────

    /// <summary>
    /// A disabled profile is never returned even when its MatchLabels cover all required labels.
    /// </summary>
    [Fact]
    public void DisabledProfile_NeverReturned()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "disabled", DisplayName = "Disabled",
                AgentProviderConfigId = "agent-1", Enabled = false,
                MatchLabels = ["kiro", "dotnet", "dotnet10"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["kiro", "dotnet", "dotnet10"]);

        result.Should().BeNull();
    }

    // ── Empty required labels ────────────────────────────────────────────────────

    /// <summary>
    /// When required labels are empty every enabled profile matches (Superset short-circuits to true).
    /// The profile with the fewest MatchLabels wins.
    /// </summary>
    [Fact]
    public void EmptyRequiredLabels_ProfileWithFewestLabelsWins()
    {
        var twoLabelProfile = new AgentProfile
        {
            Id = "two-labels",
            DisplayName = "Two Labels",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["dotnet", "dotnet10"]
        };
        var threeLabelProfile = new AgentProfile
        {
            Id = "three-labels",
            DisplayName = "Three Labels",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };
        var profiles = new[] { threeLabelProfile, twoLabelProfile };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, []);

        result.Should().NotBeNull();
        result!.Id.Should().Be("two-labels");
    }

    // ── No matching profile ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns null when no enabled profile's MatchLabels cover all required labels.
    /// </summary>
    [Fact]
    public void NoCoveringProfile_ReturnsNull()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "java-profile", DisplayName = "Java",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["kiro", "java", "java21"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["kiro", "dotnet", "dotnet10"]);

        result.Should().BeNull();
    }
}
