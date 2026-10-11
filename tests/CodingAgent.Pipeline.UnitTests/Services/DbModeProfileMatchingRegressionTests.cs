using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// REGRESSION TESTS for the DB-mode profile matching logic.
///
/// Production failure: "No profile matches required labels [dotnet, dotnet10] for DB-mode dispatch"
///
/// The fix in DispatchOrchestrationService.ResolveProfileByLabelsAsync uses this logic:
///   profiles.Where(p => p.Enabled)
///           .Where(p => requiredLabels.All(rl => p.MatchLabels.Contains(rl, OrdinalIgnoreCase)))
///
/// This verifies: requiredLabels ⊆ profile.MatchLabels (profile must COVER all required labels).
/// The OLD (wrong) logic was: profile.MatchLabels ⊆ requiredLabels.
///
/// These tests call <see cref="ProfileResolver.ResolveByRequiredLabels"/> directly.
/// </summary>
public sealed class DbModeProfileMatchingRegressionTests
{
    /// <summary>
    /// THE EXACT PRODUCTION BUG: Profile [uac, dotnet, dotnet10] must match required [dotnet, dotnet10].
    /// Old logic failed because uac ∉ [dotnet, dotnet10].
    /// New logic succeeds because [dotnet, dotnet10] ⊆ [uac, dotnet, dotnet10].
    /// </summary>
    [Fact]
    public void ProfileWithSupersetLabels_MatchesRequiredLabels()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "profile-1", DisplayName = "DotNet",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["uac", "dotnet", "dotnet10"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("profile-1");
    }

    /// <summary>
    /// Profile with exact same labels = match.
    /// </summary>
    [Fact]
    public void ProfileWithExactLabels_Matches()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "exact", DisplayName = "Exact",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["dotnet", "dotnet10"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// Profile with FEWER labels than required = no match.
    /// </summary>
    [Fact]
    public void ProfileWithFewerLabels_DoesNotMatch()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "partial", DisplayName = "Partial",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["dotnet"]  // missing dotnet10
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().BeNull();
    }

    /// <summary>
    /// Disabled profile excluded even if labels match.
    /// </summary>
    [Fact]
    public void DisabledProfile_Excluded()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "disabled", DisplayName = "Disabled",
                AgentProviderConfigId = "agent-1", Enabled = false,
                MatchLabels = ["uac", "dotnet", "dotnet10"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().BeNull();
    }

    /// <summary>
    /// Case-insensitive matching.
    /// </summary>
    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "case", DisplayName = "Case",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["UAC", "DotNet", "DotNet10"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// When multiple profiles match, the closest fit (fewest labels) wins over a profile with extra labels.
    /// The "generic" profile (2 labels, exact match) beats the "specific" profile (4 labels, superset).
    /// </summary>
    [Fact]
    public void ClosestFitProfile_WinsOverProfileWithExtraLabels()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "generic", DisplayName = "Generic",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["dotnet", "dotnet10"]
            },
            new AgentProfile
            {
                Id = "specific", DisplayName = "Specific",
                AgentProviderConfigId = "agent-2", Enabled = true,
                MatchLabels = ["uac", "dotnet", "dotnet10", "linux"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("generic");
    }

    /// <summary>
    /// Empty requiredLabels matches any enabled profile (any profile covers "nothing required").
    /// </summary>
    [Fact]
    public void EmptyRequiredLabels_MatchesAnyProfile()
    {
        var profiles = new[]
        {
            new AgentProfile
            {
                Id = "any", DisplayName = "Any",
                AgentProviderConfigId = "agent-1", Enabled = true,
                MatchLabels = ["uac", "dotnet"]
            }
        };

        var result = ProfileResolver.ResolveByRequiredLabels(profiles, []);

        result.Should().NotBeNull();
    }
}
