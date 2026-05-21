using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class LabelResolverTests
{
    [Fact]
    public void ResolveRequiredLabels_ExplicitRequiredLabels_TakesPrecedence()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = new List<string> { "kiro", "dotnet", "dotnet10" },
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "python,java"
            }
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "fallback"
        };

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet", "dotnet10" });
    }

    [Fact]
    public void ResolveRequiredLabels_SettingsDictionary_UsedWhenNoExplicitLabels()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "kiro, dotnet"
            }
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "fallback"
        };

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void ResolveRequiredLabels_PipelineDefault_UsedWhenNoRepoLabels()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo"
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "kiro, agent"
        };

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "kiro", "agent" });
    }

    [Fact]
    public void ResolveRequiredLabels_NoLabelsAnywhere_ReturnsEmpty()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo"
        };
        var pipelineConfig = new PipelineConfiguration();

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ResolveRequiredLabels_NullRepoConfig_FallsToPipelineDefault()
    {
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "default-label"
        };

        var result = LabelResolver.ResolveRequiredLabels(null, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "default-label" });
    }

    [Fact]
    public void ResolveRequiredLabels_NullRepoConfig_NullPipelineDefault_ReturnsEmpty()
    {
        var pipelineConfig = new PipelineConfiguration();

        var result = LabelResolver.ResolveRequiredLabels(null, pipelineConfig);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ResolveRequiredLabels_SettingsWithWhitespace_TrimsEntries()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "  kiro  ,  dotnet  ,  linux  "
            }
        };
        var pipelineConfig = new PipelineConfiguration();

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet", "linux" });
    }

    [Fact]
    public void ResolveRequiredLabels_EmptyExplicitLabels_FallsToSettings()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = new List<string>(), // Empty list, not null
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "kiro,dotnet"
            }
        };
        var pipelineConfig = new PipelineConfiguration();

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void ResolveRequiredLabels_WhitespaceSettingsValue_FallsToPipelineDefault()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "   "
            }
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "fallback"
        };

        var result = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        result.Should().BeEquivalentTo(new[] { "fallback" });
    }
}
