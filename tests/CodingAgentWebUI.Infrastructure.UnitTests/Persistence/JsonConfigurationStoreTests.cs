using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

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
        Assert.Equal("./workspaces", config.WorkspaceBaseDirectory);
    }

    [Fact]
    public async Task SaveThenLoad_PipelineConfig_RoundTrips()
    {
        var original = new PipelineConfiguration
        {
            MaxRetries = 5,
            AgentTimeout = TimeSpan.FromMinutes(45),
            WorkspaceBaseDirectory = "/tmp/workspaces",
            FailedWorkspaceRetentionDays = 14,
            AnalysisPrompt = "Custom analysis prompt",
            ImplementationPrompt = "Custom implementation prompt"
        };

        await _store.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        Assert.Equal(original.MaxRetries, loaded.MaxRetries);
        Assert.Equal(original.AgentTimeout, loaded.AgentTimeout);
        Assert.Equal(original.WorkspaceBaseDirectory, loaded.WorkspaceBaseDirectory);
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
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Token] = "ghp_test123",
                [ProviderSettingKeys.Owner] = "testorg",
                [ProviderSettingKeys.Repo] = "testrepo"
            }
        };

        await _store.SaveProviderConfigAsync(original, CancellationToken.None);
        var loaded = await _store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        var match = Assert.Single(loaded);
        Assert.Equal(original.Id, match.Id);
        Assert.Equal(original.Kind, match.Kind);
        Assert.Equal(original.ProviderType, match.ProviderType);
        Assert.Equal(original.DisplayName, match.DisplayName);
        Assert.Equal(original.Settings[ProviderSettingKeys.ApiUrl], match.Settings[ProviderSettingKeys.ApiUrl]);
        Assert.Equal(original.Settings[ProviderSettingKeys.Token], match.Settings[ProviderSettingKeys.Token]);
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
    public async Task SavePipelineConfigAsync_SerializesToCorrectFilePath_WithProperJsonFormatting()
    {
        // Arrange
        var config = new PipelineConfiguration
        {
            MaxRetries = 7,
            AgentTimeout = TimeSpan.FromMinutes(60),
            WorkspaceBaseDirectory = "/custom/workspaces"
        };

        // Act
        await _store.SavePipelineConfigAsync(config, CancellationToken.None);

        // Assert — file exists at expected path
        var expectedPath = Path.Combine(_tempDir, "pipeline-config.json");
        File.Exists(expectedPath).Should().BeTrue();

        // Assert — JSON is properly formatted (indented, camelCase)
        var json = await File.ReadAllTextAsync(expectedPath);
        json.Should().Contain("\"maxRetries\": 7");
        json.Should().Contain("\"workspaceBaseDirectory\": \"/custom/workspaces\"");

        // Verify indentation (WriteIndented = true)
        json.Should().Contain("\n");
        var lines = json.Split('\n');
        lines.Should().HaveCountGreaterThan(1, "JSON should be indented across multiple lines");
    }

    [Fact]
    public async Task UpdatePipelineConfigAsync_LoadsTransformsAndSaves()
    {
        // Arrange — save an initial config
        var initial = new PipelineConfiguration
        {
            MaxRetries = 2,
            AgentTimeout = TimeSpan.FromMinutes(10),
            WorkspaceBaseDirectory = "/initial"
        };
        await _store.SavePipelineConfigAsync(initial, CancellationToken.None);

        // Act — update via transform
        await _store.UpdatePipelineConfigAsync(
            existing => existing with { MaxRetries = 10, WorkspaceBaseDirectory = "/updated" },
            CancellationToken.None);

        // Assert — reload and verify transform was applied
        var loaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(10);
        loaded.WorkspaceBaseDirectory.Should().Be("/updated");
        // Verify untouched fields remain from original
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task UpdatePipelineConfigAsync_WhenNoFileExists_CreatesFromDefault()
    {
        // Arrange — no file exists yet

        // Act — update from default
        await _store.UpdatePipelineConfigAsync(
            existing => existing with { MaxRetries = 99 },
            CancellationToken.None);

        // Assert — config was created with transformed default
        var loaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(99);
        // Default values for untouched fields
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task DeleteProviderConfigAsync_ExistingConfig_RemovesFileFromDisk()
    {
        // Arrange — save a provider config
        var config = new ProviderConfig
        {
            Id = "disk-check",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "Disk Check Provider"
        };
        await _store.SaveProviderConfigAsync(config, CancellationToken.None);

        var filePath = Path.Combine(_tempDir, "providers", "pipeline", "disk-check.json");
        File.Exists(filePath).Should().BeTrue("file should exist after save");

        // Act
        await _store.DeleteProviderConfigAsync("disk-check", ProviderKind.Pipeline, CancellationToken.None);

        // Assert — file is physically removed from disk
        File.Exists(filePath).Should().BeFalse("file should be removed after delete");
    }

    [Fact]
    public async Task DeleteProviderConfigAsync_NonExistentConfig_DoesNotThrow()
    {
        // Arrange — ensure the provider directory doesn't even exist
        var dirPath = Path.Combine(_tempDir, "providers", "repository");
        Directory.Exists(dirPath).Should().BeFalse();

        // Act & Assert — should complete without throwing
        var act = () => _store.DeleteProviderConfigAsync("non-existent-id", ProviderKind.Repository, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Constructor_CleansUpOrphanedTmpFiles()
    {
        // Arrange — create .tmp files in the base directory and subdirectories
        var tmpFile1 = Path.Combine(_tempDir, "pipeline-config.json.abc123.tmp");
        var tmpFile2 = Path.Combine(_tempDir, "providers", "issue", "some-file.json.def456.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(tmpFile2)!);
        File.WriteAllText(tmpFile1, "orphaned content");
        File.WriteAllText(tmpFile2, "orphaned content");

        // Also create a real json file that should NOT be deleted
        var realFile = Path.Combine(_tempDir, "pipeline-config.json");
        File.WriteAllText(realFile, "{}");

        // Act — constructing a new store triggers cleanup
        var _ = new JsonConfigurationStore(_tempDir);

        // Assert — tmp files are deleted, real file remains
        File.Exists(tmpFile1).Should().BeFalse("orphaned .tmp file should be cleaned up");
        File.Exists(tmpFile2).Should().BeFalse("orphaned .tmp file in subdirectory should be cleaned up");
        File.Exists(realFile).Should().BeTrue("non-tmp files should not be deleted");
    }

    [Fact]
    public void Constructor_NonExistentDirectory_DoesNotThrow()
    {
        // Arrange — use a directory that doesn't exist
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"non-existent-{Guid.NewGuid()}");

        // Act & Assert — should not throw
        var store = new JsonConfigurationStore(nonExistentDir);
        Assert.NotNull(store);
    }

    [Fact]
    public async Task SaveAgentProfileAsync_SerializesWithCorrectFilenameBasedOnId()
    {
        // Arrange
        var profileId = "my-custom-profile-id";
        var profile = new AgentProfile
        {
            Id = profileId,
            DisplayName = "Test Profile",
            AgentProviderConfigId = "provider-123",
            MatchLabels = ["dotnet", "csharp"],
            Enabled = true,
            Priority = 5
        };

        // Act
        await _store.SaveAgentProfileAsync(profile, CancellationToken.None);

        // Assert — file is saved at profiles/{id}.json
        var expectedPath = Path.Combine(_tempDir, "profiles", $"{profileId}.json");
        File.Exists(expectedPath).Should().BeTrue("profile should be saved with ID as filename");

        // Verify content is valid JSON with expected data
        var json = await File.ReadAllTextAsync(expectedPath);
        json.Should().Contain("\"displayName\": \"Test Profile\"");
        json.Should().Contain("\"agentProviderConfigId\": \"provider-123\"");
    }
}
