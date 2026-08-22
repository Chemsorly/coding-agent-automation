using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Property 8: Configuration store round-trip preserves data
/// Feature: automated-dev-pipeline, Property 8: Configuration store round-trip preserves data
/// Validates: Requirements 9.5
/// Uses InMemoryConfigurationStore (promoted from E2ETests by Spec 041).
/// </summary>
public class ConfigurationStorePropertyTests
{
    private static InMemoryConfigurationStore CreateStore() => new InMemoryConfigurationStore();

    /// <summary>
    /// Property 8a: Saving then loading a PipelineConfiguration produces an equivalent object.
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void PipelineConfig_RoundTrip_PreservesData(
        int maxRetries,
        NonEmptyString workspaceDir)
    {
        // Constrain to reasonable values
        var clampedRetries = Math.Clamp(Math.Abs(maxRetries), 0, 100);
        var timeoutMinutes = Math.Clamp(Math.Abs(maxRetries % 120) + 1, 1, 120);
        var original = new PipelineConfiguration
        {
            MaxRetries = clampedRetries,
            AgentTimeout = TimeSpan.FromMinutes(timeoutMinutes),
            WorkspaceBaseDirectory = workspaceDir.Get,
            BlacklistedPaths = new[] { ".agent", ".github", $".custom-{Math.Abs(maxRetries % 10)}" },
        };

        var store = CreateStore();

        store.SavePipelineConfigAsync(original, CancellationToken.None).GetAwaiter().GetResult();
        var loaded = store.LoadPipelineConfigAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(original.MaxRetries, loaded.MaxRetries);
        Assert.Equal(original.AgentTimeout, loaded.AgentTimeout);
        Assert.Equal(original.WorkspaceBaseDirectory, loaded.WorkspaceBaseDirectory);
        Assert.Equal(original.BlacklistedPaths, loaded.BlacklistedPaths);
    }

    /// <summary>
    /// Property 8b: Saving then loading a ProviderConfig produces an equivalent object.
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ProviderConfig_RoundTrip_PreservesData(
        byte kindSeed,
        NonEmptyString providerType,
        NonEmptyString displayName,
        NonEmptyString settingKey,
        NonEmptyString settingValue)
    {
        var kinds = Enum.GetValues<ProviderKind>();
        var kind = kinds[kindSeed % kinds.Length];

        var id = Guid.NewGuid().ToString();
        var original = new ProviderConfig
        {
            Id = id,
            Kind = kind,
            ProviderType = providerType.Get,
            DisplayName = displayName.Get,
            Settings = new Dictionary<string, string>
            {
                [settingKey.Get] = settingValue.Get
            }
        };

        var store = CreateStore();

        store.SaveProviderConfigAsync(original, CancellationToken.None).GetAwaiter().GetResult();
        var loaded = store.LoadProviderConfigsAsync(kind, CancellationToken.None).GetAwaiter().GetResult();

        var match = Assert.Single(loaded, c => c.Id == id);
        Assert.Equal(original.Kind, match.Kind);
        Assert.Equal(original.ProviderType, match.ProviderType);
        Assert.Equal(original.DisplayName, match.DisplayName);
        Assert.Equal(original.Settings[settingKey.Get], match.Settings[settingKey.Get]);
    }
}
