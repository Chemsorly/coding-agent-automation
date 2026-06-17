using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.Persistence;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for ReviewerConfiguration persistence in JsonConfigurationStore.
/// Validates Requirements 2.1, 2.2, 2.3, 2.4.
/// </summary>
public class ReviewerConfigurationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigurationStore _store;

    public ReviewerConfigurationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"reviewer-store-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigurationStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SaveThenLoadThenDelete_ReviewerConfig_RoundTrips()
    {
        // Arrange
        var original = new ReviewerConfiguration
        {
            Id = "test-reviewer-1",
            DisplayName = "DotNet Reviewers",
            MatchLabels = ["dotnet", "csharp"],
            Agents =
            [
                new ReviewAgent { Name = "Correctness", Prompt = "Review for correctness issues" },
                new ReviewAgent { Name = "Security", Prompt = "Review for security vulnerabilities" }
            ],
            Enabled = true,
            ExecutionOrder = 10
        };

        // Act - Save
        await _store.SaveReviewerConfigAsync(original, CancellationToken.None);

        // Act - Load
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);

        // Assert - Load returns saved config
        var match = Assert.Single(loaded);
        Assert.Equal(original.Id, match.Id);
        Assert.Equal(original.DisplayName, match.DisplayName);
        Assert.Equal(original.MatchLabels, match.MatchLabels);
        Assert.Equal(original.Agents.Count, match.Agents.Count);
        Assert.Equal(original.Agents[0].Name, match.Agents[0].Name);
        Assert.Equal(original.Agents[0].Prompt, match.Agents[0].Prompt);
        Assert.Equal(original.Agents[1].Name, match.Agents[1].Name);
        Assert.Equal(original.Agents[1].Prompt, match.Agents[1].Prompt);
        Assert.Equal(original.Enabled, match.Enabled);
        Assert.Equal(original.ExecutionOrder, match.ExecutionOrder);

        // Act - Delete
        await _store.DeleteReviewerConfigAsync(original.Id, CancellationToken.None);

        // Assert - Deleted config is gone
        var afterDelete = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task LoadReviewerConfigs_MissingDirectory_ReturnsEmptyList()
    {
        // The reviewers directory does not exist in the temp dir
        var configs = await _store.LoadReviewerConfigsAsync(CancellationToken.None);

        Assert.Empty(configs);
    }

    [Fact]
    public async Task SaveReviewerConfig_MissingDirectory_CreatesDirectoryOnFirstSave()
    {
        // Arrange - verify directory doesn't exist
        var reviewersDir = Path.Combine(_tempDir, "reviewers");
        Assert.False(Directory.Exists(reviewersDir));

        var config = new ReviewerConfiguration
        {
            Id = "first-save",
            DisplayName = "First Config",
            Agents = [new ReviewAgent { Name = "Agent1", Prompt = "Do review" }]
        };

        // Act
        await _store.SaveReviewerConfigAsync(config, CancellationToken.None);

        // Assert - directory was created and file exists
        Assert.True(Directory.Exists(reviewersDir));
        Assert.True(File.Exists(Path.Combine(reviewersDir, "first-save.json")));

        // Verify it can be loaded back
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Single(loaded);
        Assert.Equal("first-save", loaded[0].Id);
    }

    [Fact]
    public async Task LoadReviewerConfigs_CorruptedJsonFile_SkipsItAndReturnsValidOnes()
    {
        // Arrange - create the reviewers directory with a corrupted file
        var reviewersDir = Path.Combine(_tempDir, "reviewers");
        Directory.CreateDirectory(reviewersDir);
        await File.WriteAllTextAsync(
            Path.Combine(reviewersDir, "corrupted.json"),
            "{ this is not valid json at all!!!");

        // Also save a valid config
        var validConfig = new ReviewerConfiguration
        {
            Id = "valid-config",
            DisplayName = "Valid Reviewer",
            Agents = [new ReviewAgent { Name = "ValidAgent", Prompt = "Valid prompt" }],
            Enabled = true,
            ExecutionOrder = 5
        };
        await _store.SaveReviewerConfigAsync(validConfig, CancellationToken.None);

        // Act
        var configs = await _store.LoadReviewerConfigsAsync(CancellationToken.None);

        // Assert - only the valid config is returned, corrupted one is skipped
        Assert.Single(configs);
        Assert.Equal("valid-config", configs[0].Id);
        Assert.Equal("Valid Reviewer", configs[0].DisplayName);
    }

    [Fact]
    public async Task SaveReviewerConfig_UsesJsonCamelCaseAndIndented()
    {
        // Arrange
        var config = new ReviewerConfiguration
        {
            Id = "format-test",
            DisplayName = "Format Test",
            MatchLabels = ["python"],
            Agents = [new ReviewAgent { Name = "PythonLinter", Prompt = "Lint Python code" }],
            Enabled = true,
            ExecutionOrder = 3
        };

        // Act
        await _store.SaveReviewerConfigAsync(config, CancellationToken.None);

        // Assert - verify JSON format (camelCase, indented)
        var filePath = Path.Combine(_tempDir, "reviewers", "format-test.json");
        var json = await File.ReadAllTextAsync(filePath);

        // Should use camelCase property names
        Assert.Contains("\"displayName\"", json);
        Assert.Contains("\"matchLabels\"", json);
        Assert.Contains("\"executionOrder\"", json);

        // Should NOT use PascalCase
        Assert.DoesNotContain("\"DisplayName\"", json);
        Assert.DoesNotContain("\"MatchLabels\"", json);
        Assert.DoesNotContain("\"ExecutionOrder\"", json);

        // Should be indented (contains newlines and spaces)
        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public async Task DeleteReviewerConfig_NonExistentId_DoesNotThrow()
    {
        // Should not throw when deleting a non-existent config
        await _store.DeleteReviewerConfigAsync("does-not-exist", CancellationToken.None);
    }

    [Fact]
    public async Task SaveReviewerConfig_OverwritesExistingConfig()
    {
        // Arrange - save initial config
        var original = new ReviewerConfiguration
        {
            Id = "overwrite-test",
            DisplayName = "Original Name",
            Agents = [new ReviewAgent { Name = "Agent1", Prompt = "Original prompt" }],
            ExecutionOrder = 1
        };
        await _store.SaveReviewerConfigAsync(original, CancellationToken.None);

        // Act - save updated config with same Id
        var updated = new ReviewerConfiguration
        {
            Id = "overwrite-test",
            DisplayName = "Updated Name",
            Agents = [new ReviewAgent { Name = "Agent2", Prompt = "Updated prompt" }],
            ExecutionOrder = 5
        };
        await _store.SaveReviewerConfigAsync(updated, CancellationToken.None);

        // Assert - only one config exists with updated values
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        var match = Assert.Single(loaded);
        Assert.Equal("Updated Name", match.DisplayName);
        Assert.Equal("Agent2", match.Agents[0].Name);
        Assert.Equal(5, match.ExecutionOrder);
    }

    [Fact]
    public async Task LoadReviewerConfigs_MultipleFiles_ReturnsAll()
    {
        // Arrange - save multiple configs
        var config1 = new ReviewerConfiguration
        {
            Id = "config-1",
            DisplayName = "Global Reviewers",
            MatchLabels = [],
            Agents = [new ReviewAgent { Name = "Correctness", Prompt = "Check correctness" }],
            ExecutionOrder = 0
        };
        var config2 = new ReviewerConfiguration
        {
            Id = "config-2",
            DisplayName = "DotNet Reviewers",
            MatchLabels = ["dotnet"],
            Agents = [new ReviewAgent { Name = "DotNetSpecialist", Prompt = "Check .NET issues" }],
            ExecutionOrder = 10
        };
        var config3 = new ReviewerConfiguration
        {
            Id = "config-3",
            DisplayName = "Python Reviewers",
            MatchLabels = ["python"],
            Agents = [new ReviewAgent { Name = "PythonLinter", Prompt = "Lint Python" }],
            ExecutionOrder = 20
        };

        await _store.SaveReviewerConfigAsync(config1, CancellationToken.None);
        await _store.SaveReviewerConfigAsync(config2, CancellationToken.None);
        await _store.SaveReviewerConfigAsync(config3, CancellationToken.None);

        // Act
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);

        // Assert - all three configs are returned
        Assert.Equal(3, loaded.Count);
        Assert.Contains(loaded, c => c.Id == "config-1");
        Assert.Contains(loaded, c => c.Id == "config-2");
        Assert.Contains(loaded, c => c.Id == "config-3");
    }

    // --- ResetReviewerConfigsToDefaultAsync tests ---

    [Fact]
    public async Task ResetReviewerConfigsToDefault_RemovesExistingAndWritesDefaults()
    {
        // Arrange — save two custom configs
        var custom1 = new ReviewerConfiguration
        {
            Id = "custom-1",
            DisplayName = "Custom Config 1",
            MatchLabels = ["java"],
            Agents = [new ReviewAgent { Name = "JavaLinter", Prompt = "Lint Java" }],
            ExecutionOrder = 5
        };
        var custom2 = new ReviewerConfiguration
        {
            Id = "custom-2",
            DisplayName = "Custom Config 2",
            Agents = [new ReviewAgent { Name = "GoReviewer", Prompt = "Review Go" }],
            ExecutionOrder = 10
        };
        await _store.SaveReviewerConfigAsync(custom1, CancellationToken.None);
        await _store.SaveReviewerConfigAsync(custom2, CancellationToken.None);

        // Act
        await _store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        // Assert — custom configs gone, defaults present
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Equal(PipelineConfiguration.DefaultReviewerConfigurations.Count, loaded.Count);

        var defaultConfig = Assert.Single(loaded);
        Assert.Equal(PipelineConfiguration.DefaultReviewerConfigurationId, defaultConfig.Id);
        Assert.Equal("Default Reviewers", defaultConfig.DisplayName);
        Assert.Empty(defaultConfig.MatchLabels);
        Assert.True(defaultConfig.Enabled);
        Assert.Equal(PipelineConfiguration.DefaultReviewAgents.Count, defaultConfig.Agents.Count);

        // Old custom files should not exist on disk
        var reviewersDir = Path.Combine(_tempDir, "reviewers");
        Assert.False(File.Exists(Path.Combine(reviewersDir, "custom-1.json")));
        Assert.False(File.Exists(Path.Combine(reviewersDir, "custom-2.json")));
    }

    [Fact]
    public async Task ResetReviewerConfigsToDefault_NoExistingDirectory_CreatesAndWritesDefaults()
    {
        // Arrange — ensure no reviewers directory exists
        var reviewersDir = Path.Combine(_tempDir, "reviewers");
        Assert.False(Directory.Exists(reviewersDir));

        // Act
        await _store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        // Assert — defaults were written
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Equal(PipelineConfiguration.DefaultReviewerConfigurations.Count, loaded.Count);
        Assert.Equal(PipelineConfiguration.DefaultReviewerConfigurationId, loaded[0].Id);
    }

    [Fact]
    public async Task ResetReviewerConfigsToDefault_InvalidatesCache()
    {
        // Arrange — prime the cache by loading
        var custom = new ReviewerConfiguration
        {
            Id = "cached-config",
            DisplayName = "Will Be Cached",
            Agents = [new ReviewAgent { Name = "Agent", Prompt = "Prompt" }]
        };
        await _store.SaveReviewerConfigAsync(custom, CancellationToken.None);
        var beforeReset = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Single(beforeReset);
        Assert.Equal("cached-config", beforeReset[0].Id);

        // Act
        await _store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        // Assert — subsequent load returns defaults, not cached custom config
        var afterReset = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.DoesNotContain(afterReset, c => c.Id == "cached-config");
        Assert.Contains(afterReset, c => c.Id == PipelineConfiguration.DefaultReviewerConfigurationId);
    }

    [Fact]
    public async Task ResetReviewerConfigsToDefault_AgentsMirrorDefaultReviewAgents()
    {
        // Act
        await _store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        // Assert — agents in the written config match DefaultReviewAgents exactly
        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        var config = Assert.Single(loaded);

        for (var i = 0; i < PipelineConfiguration.DefaultReviewAgents.Count; i++)
        {
            Assert.Equal(PipelineConfiguration.DefaultReviewAgents[i].Name, config.Agents[i].Name);
            Assert.Equal(PipelineConfiguration.DefaultReviewAgents[i].Prompt, config.Agents[i].Prompt);
        }
    }
}
