using KiroWebUI.Pipeline.Models;
using KiroWebUI.Infrastructure.GitHub;
using KiroWebUI.Infrastructure.Agent;
using KiroWebUI.Infrastructure.Persistence;
using KiroWebUI.Infrastructure;
using Xunit;

namespace KiroWebUI.Infrastructure.UnitTests;

public class JsonConfigurationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigurationStore _store;

    public JsonConfigurationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-store-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigurationStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadPipelineConfig_MissingFile_ReturnsDefaults()
    {
        var config = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        Assert.Equal(3, config.MaxRetries);
        Assert.Equal(TimeSpan.FromMinutes(30), config.AgentTimeout);
        Assert.Equal(80.0, config.MinCoverageThreshold);
        Assert.False(config.SecurityScanEnabled);
        Assert.Equal("./workspaces", config.WorkspaceBaseDirectory);
    }

    [Fact]
    public async Task SaveThenLoad_PipelineConfig_RoundTrips()
    {
        var original = new PipelineConfiguration
        {
            MaxRetries = 5,
            AgentTimeout = TimeSpan.FromMinutes(45),
            MinCoverageThreshold = 90.0,
            SecurityScanEnabled = true,
            WorkspaceBaseDirectory = "/tmp/workspaces",
            CleanupSuccessfulWorkspaces = false,
            FailedWorkspaceRetentionDays = 14,
            AnalysisPrompt = "Custom analysis prompt",
            ImplementationPrompt = "Custom implementation prompt"
        };

        await _store.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        Assert.Equal(original.MaxRetries, loaded.MaxRetries);
        Assert.Equal(original.AgentTimeout, loaded.AgentTimeout);
        Assert.Equal(original.MinCoverageThreshold, loaded.MinCoverageThreshold);
        Assert.Equal(original.SecurityScanEnabled, loaded.SecurityScanEnabled);
        Assert.Equal(original.WorkspaceBaseDirectory, loaded.WorkspaceBaseDirectory);
        Assert.Equal(original.CleanupSuccessfulWorkspaces, loaded.CleanupSuccessfulWorkspaces);
        Assert.Equal(original.FailedWorkspaceRetentionDays, loaded.FailedWorkspaceRetentionDays);
        Assert.Equal(original.AnalysisPrompt, loaded.AnalysisPrompt);
        Assert.Equal(original.ImplementationPrompt, loaded.ImplementationPrompt);
    }

    [Fact]
    public async Task SaveThenLoad_ProviderConfig_RoundTrips()
    {
        var original = new ProviderConfig
        {
            Id = "test-provider-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My GitHub",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["token"] = "ghp_test123",
                ["owner"] = "testorg",
                ["repo"] = "testrepo"
            }
        };

        await _store.SaveProviderConfigAsync(original, CancellationToken.None);
        var loaded = await _store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        var match = Assert.Single(loaded);
        Assert.Equal(original.Id, match.Id);
        Assert.Equal(original.Kind, match.Kind);
        Assert.Equal(original.ProviderType, match.ProviderType);
        Assert.Equal(original.DisplayName, match.DisplayName);
        Assert.Equal(original.Settings["apiUrl"], match.Settings["apiUrl"]);
        Assert.Equal(original.Settings["token"], match.Settings["token"]);
    }

    [Fact]
    public async Task LoadProviderConfigs_MissingDirectory_ReturnsEmpty()
    {
        var configs = await _store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        Assert.Empty(configs);
    }

    [Fact]
    public async Task DeleteProviderConfig_RemovesFile()
    {
        var config = new ProviderConfig
        {
            Id = "to-delete",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Delete Me"
        };

        await _store.SaveProviderConfigAsync(config, CancellationToken.None);
        var beforeDelete = await _store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        Assert.Single(beforeDelete);

        await _store.DeleteProviderConfigAsync("to-delete", ProviderKind.Repository, CancellationToken.None);
        var afterDelete = await _store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task LoadPipelineConfig_MalformedJson_ReturnsDefaults()
    {
        var path = Path.Combine(_tempDir, "pipeline-config.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json!!!");

        var config = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        // Should return defaults, not throw
        Assert.Equal(3, config.MaxRetries);
    }

    [Fact]
    public async Task LoadProviderConfigs_MalformedJsonFile_SkipsIt()
    {
        // Create the provider directory with a malformed file
        var dir = Path.Combine(_tempDir, "providers", "issue");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "bad.json"), "not json");

        // Also save a valid one
        var valid = new ProviderConfig
        {
            Id = "valid-one",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Valid"
        };
        await _store.SaveProviderConfigAsync(valid, CancellationToken.None);

        var configs = await _store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        // Should load the valid one and skip the malformed one
        Assert.Single(configs, c => c.Id == "valid-one");
    }

    [Fact]
    public async Task DeleteProviderConfig_NonExistentId_DoesNotThrow()
    {
        // Should not throw when deleting a non-existent config
        await _store.DeleteProviderConfigAsync("does-not-exist", ProviderKind.Agent, CancellationToken.None);
    }

    [Fact]
    public async Task SaveThenLoad_BrainWarnings_RoundTrips()
    {
        var original = new PipelineConfiguration
        {
            BrainWarnings = new Dictionary<string, IReadOnlyList<string>>
            {
                ["brain-1"] = new[] { "session log", "log.md entry" }
            }
        };

        await _store.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        Assert.Single(loaded.BrainWarnings);
        Assert.True(loaded.BrainWarnings.ContainsKey("brain-1"));
        Assert.Equal(new[] { "session log", "log.md entry" }, loaded.BrainWarnings["brain-1"]);
    }

    [Fact]
    public async Task UpdatePipelineConfig_ClearsBrainWarnings_WhenEmpty()
    {
        var initial = new PipelineConfiguration
        {
            BrainWarnings = new Dictionary<string, IReadOnlyList<string>>
            {
                ["brain-1"] = new[] { "session log" }
            }
        };
        await _store.SavePipelineConfigAsync(initial, CancellationToken.None);

        await _store.UpdatePipelineConfigAsync(config =>
        {
            var dict = new Dictionary<string, IReadOnlyList<string>>(config.BrainWarnings);
            dict.Remove("brain-1");
            return config with { BrainWarnings = dict };
        }, CancellationToken.None);

        var loaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Empty(loaded.BrainWarnings);
    }
}
