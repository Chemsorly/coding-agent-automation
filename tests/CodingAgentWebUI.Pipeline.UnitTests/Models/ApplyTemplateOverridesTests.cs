using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="PipelineConfiguration.ApplyTemplateOverrides"/>.
/// Verifies template matching by repo+brain provider IDs, BrainReadOnly one-directional
/// override, and blacklist delegation to <see cref="PipelineConfiguration.ApplyBlacklistOverride"/>.
/// </summary>
public class ApplyTemplateOverridesTests
{
    [Fact]
    public void MatchingTemplate_BrainReadOnlyTrue_AppliesOverride()
    {
        var config = TestPipelineConfig.Default();
        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "Test Template",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                BrainProviderId = "brain-1",
                BrainReadOnly = true,
            }
        };
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                DisplayName = "Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", "brain-1", providerConfigs, templates);

        result.BrainReadOnly.Should().BeTrue();
    }

    [Fact]
    public void MatchingTemplate_BrainReadOnlyFalse_NoChange()
    {
        var config = TestPipelineConfig.Default();
        config.BrainReadOnly.Should().BeFalse(); // precondition

        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "Test Template",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                BrainProviderId = "brain-1",
                BrainReadOnly = false,
            }
        };
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                DisplayName = "Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", "brain-1", providerConfigs, templates);

        result.BrainReadOnly.Should().BeFalse();
    }

    [Fact]
    public void NoMatchingTemplate_NoChange()
    {
        var config = TestPipelineConfig.Default();
        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "Other Template",
                IssueProviderId = "issue-1",
                RepoProviderId = "other-repo",
                BrainProviderId = "other-brain",
                BrainReadOnly = true,
            }
        };
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                DisplayName = "Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", "brain-1", providerConfigs, templates);

        result.BrainReadOnly.Should().BeFalse();
    }

    [Fact]
    public void MatchingTemplate_AppliesBlacklistFromRepoProvider()
    {
        var config = TestPipelineConfig.Default();
        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "Test Template",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                BrainProviderId = "brain-1",
                BrainReadOnly = false,
            }
        };
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                DisplayName = "Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                BlacklistedPaths = new List<string> { "vendor", "dist" },
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", "brain-1", providerConfigs, templates);

        result.BlacklistedPaths.Should().BeEquivalentTo(new[] { "vendor", "dist" });
    }

    [Fact]
    public void EmptyTemplates_NoChange()
    {
        var config = TestPipelineConfig.Default();
        var templates = Array.Empty<PipelineJobTemplate>();
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                DisplayName = "Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", "brain-1", providerConfigs, templates);

        result.BrainReadOnly.Should().BeFalse();
        result.BlacklistedPaths.Should().BeEquivalentTo(config.BlacklistedPaths);
    }

    [Fact]
    public void NullBrainProviderId_MatchesTemplateWithNullBrain()
    {
        var config = TestPipelineConfig.Default();
        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "No Brain Template",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                BrainProviderId = null,
                BrainReadOnly = true,
            }
        };
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "repo-1",
                DisplayName = "Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", null, providerConfigs, templates);

        result.BrainReadOnly.Should().BeTrue();
    }

    [Fact]
    public void NoMatchingRepoProvider_BlacklistUnchanged()
    {
        var config = TestPipelineConfig.Default();
        var originalBlacklist = config.BlacklistedPaths;
        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "Test Template",
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                BrainProviderId = "brain-1",
                BrainReadOnly = true,
            }
        };
        // Provider configs don't include repo-1
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "other-repo",
                DisplayName = "Other Repo",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                BlacklistedPaths = new List<string> { "should-not-apply" },
            }
        };

        var result = PipelineConfiguration.ApplyTemplateOverrides(
            config, "repo-1", "brain-1", providerConfigs, templates);

        // BrainReadOnly applied (template matched)
        result.BrainReadOnly.Should().BeTrue();
        // Blacklist unchanged (no matching provider config for repo-1)
        result.BlacklistedPaths.Should().BeEquivalentTo(originalBlacklist);
    }
}
