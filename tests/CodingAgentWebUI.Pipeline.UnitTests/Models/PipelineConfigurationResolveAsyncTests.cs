using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="PipelineConfiguration.ResolveAsync"/> static methods.
/// Verifies the resolution chain: Global → Project overrides → Template overrides.
/// </summary>
public class PipelineConfigurationResolveAsyncTests
{
    [Fact]
    public async Task ResolveAsync_AppliesProjectAndTemplateOverridesInOrder()
    {
        // TODO: This test uses non-overlapping properties (MaxRetries from project, BrainReadOnly from template).
        // It would pass even if resolution order were reversed. Add a property set by BOTH sources with different values to prove ordering.

        // Arrange: global config with BrainReadOnly = false
        var globalConfig = new PipelineConfiguration();
        globalConfig.BrainReadOnly.Should().BeFalse(); // baseline

        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "Test",
            MaxRetries = 5
        };

        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "Test Template",
                IssueProviderId = "ip-1",
                RepoProviderId = "repo-1",
                BrainProviderId = "brain-1",
                BrainReadOnly = true // This should override config.BrainReadOnly to true
            }
        };

        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Test Repo"
            }
        };

        // Act
        var result = await PipelineConfiguration.ResolveAsync(
            ct => Task.FromResult(globalConfig),
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            project,
            "repo-1",
            "brain-1",
            providerConfigs,
            CancellationToken.None);

        // Assert: project override applied
        result.MaxRetries.Should().Be(5);
        // Assert: template override applied (BrainReadOnly = true from template)
        result.BrainReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_PreLoaded_SkipsConfigLoad()
    {
        // Arrange: pre-loaded config
        var preLoadedConfig = new PipelineConfiguration { MaxRetries = 2 };

        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "Test",
            MaxRetries = 7
        };

        var templates = new List<PipelineJobTemplate>();
        var providerConfigs = new List<ProviderConfig>();

        // Act: use the pre-loaded overload
        var result = await PipelineConfiguration.ResolveAsync(
            preLoadedConfig,
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            project,
            "repo-1",
            null,
            providerConfigs,
            CancellationToken.None);

        // Assert: project override applied to the pre-loaded config
        result.MaxRetries.Should().Be(7);
    }

    [Fact]
    public async Task ResolveAsync_NullProject_ReturnsConfigWithTemplateOverridesOnly()
    {
        var globalConfig = new PipelineConfiguration();

        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "BrainReadOnly Template",
                IssueProviderId = "ip-1",
                RepoProviderId = "repo-1",
                BrainProviderId = null,
                BrainReadOnly = true
            }
        };

        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Test"
            }
        };

        var result = await PipelineConfiguration.ResolveAsync(
            ct => Task.FromResult(globalConfig),
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            null!, // TODO: null! bypasses non-nullable parameter — this relies on ApplyProjectOverrides being defensive. Consider making the parameter nullable or adding a dedicated null-handling test.
            "repo-1",
            null,
            providerConfigs,
            CancellationToken.None);

        // Template override still applied
        result.BrainReadOnly.Should().BeTrue();
    }
}
