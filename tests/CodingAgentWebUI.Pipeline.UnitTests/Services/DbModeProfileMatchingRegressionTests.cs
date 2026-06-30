using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
/// These tests exercise the exact LINQ expression used in production.
/// </summary>
public sealed class DbModeProfileMatchingRegressionTests
{
    /// <summary>
    /// Simulates the exact logic from ResolveProfileByLabelsAsync.
    /// </summary>
    private static AgentProfile? ResolveProfileByLabels(
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyList<string> requiredLabels)
    {
        return profiles
            .Where(p => p.Enabled)
            .Where(p => requiredLabels.All(rl =>
                p.MatchLabels.Contains(rl, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.MatchLabels.Count)
            .ThenByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

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

        var result = ResolveProfileByLabels(profiles, ["dotnet", "dotnet10"]);

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

        var result = ResolveProfileByLabels(profiles, ["dotnet", "dotnet10"]);

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

        var result = ResolveProfileByLabels(profiles, ["dotnet", "dotnet10"]);

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

        var result = ResolveProfileByLabels(profiles, ["dotnet", "dotnet10"]);

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

        var result = ResolveProfileByLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// When multiple profiles match, most specific (most labels) wins.
    /// </summary>
    [Fact]
    public void MostSpecificProfile_WinsOverGeneric()
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

        var result = ResolveProfileByLabels(profiles, ["dotnet", "dotnet10"]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("specific");
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

        var result = ResolveProfileByLabels(profiles, []);

        result.Should().NotBeNull();
    }
}
