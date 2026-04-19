using FsCheck;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;
using Xunit;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property 8: Configuration store round-trip preserves data
/// Feature: automated-dev-pipeline, Property 8: Configuration store round-trip preserves data
/// Validates: Requirements 9.5
/// </summary>
public class ConfigurationStorePropertyTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationStorePropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-store-pbt-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Property 8a: Saving then loading a PipelineConfiguration produces an equivalent object.
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public void PipelineConfig_RoundTrip_PreservesData(
        int maxRetries,
        bool securityScan,
        NonEmptyString workspaceDir)
    {
        // Constrain to reasonable values
        var clampedRetries = Math.Clamp(Math.Abs(maxRetries), 0, 100);
        var clampedCoverage = Math.Round(Math.Abs(maxRetries % 101) * 1.0, 1);
        var timeoutMinutes = Math.Clamp(Math.Abs(maxRetries % 120) + 1, 1, 120);
        var blacklistMode = (Math.Abs(maxRetries) % 2 == 0) ? BlacklistMode.WarnAndExclude : BlacklistMode.Fail;

        var original = new PipelineConfiguration
        {
            MaxRetries = clampedRetries,
            AgentTimeout = TimeSpan.FromMinutes(timeoutMinutes),
            MinCoverageThreshold = clampedCoverage,
            SecurityScanEnabled = securityScan,
            WorkspaceBaseDirectory = workspaceDir.Get,
            BlacklistedPaths = new[] { ".kiro", ".github", $".custom-{Math.Abs(maxRetries % 10)}" },
            BlacklistMode = blacklistMode
        };

        var store = new JsonConfigurationStore(_tempDir);

        store.SavePipelineConfigAsync(original, CancellationToken.None).GetAwaiter().GetResult();
        var loaded = store.LoadPipelineConfigAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(original.MaxRetries, loaded.MaxRetries);
        Assert.Equal(original.AgentTimeout, loaded.AgentTimeout);
        Assert.Equal(original.MinCoverageThreshold, loaded.MinCoverageThreshold);
        Assert.Equal(original.SecurityScanEnabled, loaded.SecurityScanEnabled);
        Assert.Equal(original.WorkspaceBaseDirectory, loaded.WorkspaceBaseDirectory);
        Assert.Equal(original.BlacklistedPaths, loaded.BlacklistedPaths);
        Assert.Equal(original.BlacklistMode, loaded.BlacklistMode);
    }

    /// <summary>
    /// Property 8b: Saving then loading a ProviderConfig produces an equivalent object.
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 100)]
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

        var store = new JsonConfigurationStore(_tempDir);

        store.SaveProviderConfigAsync(original, CancellationToken.None).GetAwaiter().GetResult();
        var loaded = store.LoadProviderConfigsAsync(kind, CancellationToken.None).GetAwaiter().GetResult();

        var match = Assert.Single(loaded, c => c.Id == id);
        Assert.Equal(original.Kind, match.Kind);
        Assert.Equal(original.ProviderType, match.ProviderType);
        Assert.Equal(original.DisplayName, match.DisplayName);
        Assert.Equal(original.Settings[settingKey.Get], match.Settings[settingKey.Get]);
    }
}
