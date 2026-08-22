using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests for generic configuration loading via IConfigurationStore.
/// Feature: 018-encapsulation-improvements, Property 6: Generic configuration loading completeness
/// Uses InMemoryConfigurationStore (promoted from E2ETests by Spec 041).
/// </summary>
public class ConfigurationLoadingPropertyTests
{
    private static InMemoryConfigurationStore CreateStore() => new InMemoryConfigurationStore();

    /// <summary>
    /// Property 6a: For any N valid AgentProfiles saved to the store,
    /// LoadAgentProfilesAsync returns exactly N items.
    /// **Validates: Requirements 31.1, 31.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LoadAllProfiles_ReturnsExactlyN_SavedItems(PositiveInt countSeed)
    {
        // Constrain to reasonable count (1-20)
        var validCount = (countSeed.Get % 20) + 1;

        var store = CreateStore();

        for (var i = 0; i < validCount; i++)
        {
            var profile = new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"Profile-{i}",
                AgentProviderConfigId = Guid.NewGuid().ToString(),
                Enabled = true,
                Priority = i
            };
            store.SaveAgentProfileAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
        }

        var result = store.LoadAgentProfilesAsync(CancellationToken.None).GetAwaiter().GetResult();

        result.Count.Should().Be(validCount);
    }

    /// <summary>
    /// Property 6b: Saved agent profiles are all returned by LoadAgentProfilesAsync;
    /// no profile is lost or duplicated.
    /// **Validates: Requirements 31.1, 31.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void LoadAllProfiles_ReturnsAllSavedProfiles(
        PositiveInt validSeed)
    {
        var count = (validSeed.Get % 10) + 1;
        var store = CreateStore();

        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid().ToString();
            ids.Add(id);
            store.SaveAgentProfileAsync(
                new AgentProfile { Id = id, DisplayName = $"Valid-{i}", AgentProviderConfigId = "p", Enabled = true, Priority = i },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        var result = store.LoadAgentProfilesAsync(CancellationToken.None).GetAwaiter().GetResult();

        result.Count.Should().Be(count);
        foreach (var id in ids)
            result.Should().Contain(p => p.Id == id);
    }

    /// <summary>
    /// Property 6c: An empty store returns an empty list (not an exception).
    /// **Validates: Requirements 31.1, 31.2**
    /// </summary>
    [Fact]
    public async Task LoadAllProfiles_EmptyStore_ReturnsEmpty()
    {
        var store = CreateStore();
        // Clear seeded defaults
        var all = await store.LoadAgentProfilesAsync(CancellationToken.None);
        foreach (var p in all)
            await store.DeleteAgentProfileAsync(p.Id, CancellationToken.None);

        var result = await store.LoadAgentProfilesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }
}
