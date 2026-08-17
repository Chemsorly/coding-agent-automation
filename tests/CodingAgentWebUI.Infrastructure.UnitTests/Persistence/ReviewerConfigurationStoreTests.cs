using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for ReviewerConfiguration persistence via IConfigurationStore.
/// Migrated from JsonConfigurationStore to InMemoryConfigurationStore by Spec 041.
/// Validates Requirements 2.1, 2.2, 2.3, 2.4.
/// </summary>
public class ReviewerConfigurationStoreTests
{
    private static InMemoryConfigurationStore CreateStore() => new InMemoryConfigurationStore();

    [Fact]
    public async Task SaveThenLoadThenDelete_ReviewerConfig_RoundTrips()
    {
        var store = CreateStore();
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

        // Save
        await store.SaveReviewerConfigAsync(original, CancellationToken.None);

        // Load
        var loaded = await store.LoadReviewerConfigsAsync(CancellationToken.None);

        var match = Assert.Single(loaded, c => c.Id == original.Id);
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

        // Delete
        await store.DeleteReviewerConfigAsync(original.Id, CancellationToken.None);

        var afterDelete = await store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task LoadReviewerConfigs_EmptyStore_ReturnsEmptyList()
    {
        var store = CreateStore();
        var configs = await store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Empty(configs);
    }

    [Fact]
    public async Task DeleteReviewerConfig_NonExistentId_DoesNotThrow()
    {
        var store = CreateStore();
        await store.DeleteReviewerConfigAsync("does-not-exist", CancellationToken.None);
        var configs = await store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Empty(configs);
    }

    [Fact]
    public async Task SaveReviewerConfig_OverwritesExistingConfig()
    {
        var store = CreateStore();
        var original = new ReviewerConfiguration
        {
            Id = "overwrite-test",
            DisplayName = "Original Name",
            Agents = [new ReviewAgent { Name = "Agent1", Prompt = "Original prompt" }],
            ExecutionOrder = 1
        };
        await store.SaveReviewerConfigAsync(original, CancellationToken.None);

        var updated = new ReviewerConfiguration
        {
            Id = "overwrite-test",
            DisplayName = "Updated Name",
            Agents = [new ReviewAgent { Name = "Agent2", Prompt = "Updated prompt" }],
            ExecutionOrder = 5
        };
        await store.SaveReviewerConfigAsync(updated, CancellationToken.None);

        var loaded = await store.LoadReviewerConfigsAsync(CancellationToken.None);
        var match = Assert.Single(loaded);
        Assert.Equal("Updated Name", match.DisplayName);
        Assert.Equal("Agent2", match.Agents[0].Name);
        Assert.Equal(5, match.ExecutionOrder);
    }

    [Fact]
    public async Task LoadReviewerConfigs_MultipleConfigs_ReturnsAll()
    {
        var store = CreateStore();
        var config1 = new ReviewerConfiguration
        {
            Id = "config-1", DisplayName = "Global Reviewers", MatchLabels = [],
            Agents = [new ReviewAgent { Name = "Correctness", Prompt = "Check correctness" }],
            ExecutionOrder = 0
        };
        var config2 = new ReviewerConfiguration
        {
            Id = "config-2", DisplayName = "DotNet Reviewers", MatchLabels = ["dotnet"],
            Agents = [new ReviewAgent { Name = "DotNetSpecialist", Prompt = "Check .NET issues" }],
            ExecutionOrder = 10
        };
        var config3 = new ReviewerConfiguration
        {
            Id = "config-3", DisplayName = "Python Reviewers", MatchLabels = ["python"],
            Agents = [new ReviewAgent { Name = "PythonLinter", Prompt = "Lint Python" }],
            ExecutionOrder = 20
        };

        await store.SaveReviewerConfigAsync(config1, CancellationToken.None);
        await store.SaveReviewerConfigAsync(config2, CancellationToken.None);
        await store.SaveReviewerConfigAsync(config3, CancellationToken.None);

        var loaded = await store.LoadReviewerConfigsAsync(CancellationToken.None);

        Assert.Equal(3, loaded.Count);
        Assert.Contains(loaded, c => c.Id == "config-1");
        Assert.Contains(loaded, c => c.Id == "config-2");
        Assert.Contains(loaded, c => c.Id == "config-3");
    }

    [Fact]
    public async Task ResetReviewerConfigsToDefault_RemovesExistingAndWritesDefaults()
    {
        var store = CreateStore();
        var custom = new ReviewerConfiguration
        {
            Id = "custom-1", DisplayName = "Custom",
            Agents = [new ReviewAgent { Name = "Agent", Prompt = "Review" }]
        };
        await store.SaveReviewerConfigAsync(custom, CancellationToken.None);

        await store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        var loaded = await store.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Equal(PipelineConfigurationDefaults.DefaultReviewerConfigurations.Count, loaded.Count);
        Assert.Contains(loaded, c => c.Id == PipelineConfigurationDefaults.DefaultReviewerConfigurationId);
        Assert.DoesNotContain(loaded, c => c.Id == "custom-1");
    }

    [Fact]
    public async Task ResetReviewerConfigsToDefault_AgentsMirrorDefaultReviewAgents()
    {
        var store = CreateStore();
        await store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        var loaded = await store.LoadReviewerConfigsAsync(CancellationToken.None);
        var config = Assert.Single(loaded);

        for (var i = 0; i < PipelineConfigurationDefaults.DefaultReviewAgents.Count; i++)
        {
            Assert.Equal(PipelineConfigurationDefaults.DefaultReviewAgents[i].Name, config.Agents[i].Name);
            Assert.Equal(PipelineConfigurationDefaults.DefaultReviewAgents[i].Prompt, config.Agents[i].Prompt);
        }
    }
}
