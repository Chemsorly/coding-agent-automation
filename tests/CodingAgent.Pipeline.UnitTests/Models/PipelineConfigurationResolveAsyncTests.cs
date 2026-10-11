using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Models;


/// <summary>
/// Tests for <see cref="PipelineConfigurationResolver.ResolveAsync"/> static methods.
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
        var result = await PipelineConfigurationResolver.ResolveAsync(
            ct => Task.FromResult(globalConfig),
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            project,
            "repo-1",
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
        var result = await PipelineConfigurationResolver.ResolveAsync(
            preLoadedConfig,
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            project,
            "repo-1",
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

        var result = await PipelineConfigurationResolver.ResolveAsync(
            ct => Task.FromResult(globalConfig),
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            null!, // TODO: null! bypasses non-nullable parameter — this relies on ApplyProjectOverrides being defensive. Consider making the parameter nullable or adding a dedicated null-handling test.
            "repo-1",
            providerConfigs,
            CancellationToken.None);

        // Template override still applied
        result.BrainReadOnly.Should().BeTrue();
    }

    // ── MaxDecompositionSubIssues clamp — Issue #3624 ─────────────────────────

    // TODO: The tests below only cover the path where the global config itself has an invalid value.
    // The original bug scenario is a project-level override setting MaxDecompositionSubIssues to 0
    // on top of a valid global config (e.g., global = 5, PipelineProject.MaxDecompositionSubIssues = 0).
    // Add a test that passes a PipelineProject with MaxDecompositionSubIssues = 0 (and a global config
    // with a valid positive value) and asserts the resolved cap is still >= 1, to verify that
    // ClampSubIssueCap runs *after* ApplyProjectOverrides and catches the project-introduced regression.

    /// <summary>
    /// Feature: Issue #3624 — MaxDecompositionSubIssues = 0 silently fails decomposition runs.
    ///
    /// When the global config is loaded from storage with MaxDecompositionSubIssues = 0
    /// (possible via direct DB edit or a row that pre-dates validation), ResolveAsync must
    /// clamp it to at least 1 so that CreateSubIssuesStep never receives a 0 cap.
    ///
    /// This test fails before the fix (result.MaxDecompositionSubIssues == 0) and
    /// passes after it (result.MaxDecompositionSubIssues == 1).
    ///
    /// Acceptance criterion: "A test that resolves an effective config with
    /// MaxDecompositionSubIssues = 0 and asserts the effective cap is at least 1
    /// fails before the fix and passes after it."
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task ResolveAsync_WhenGlobalConfigHasNonPositiveMaxDecompositionSubIssues_ClampsToOne(int badValue)
    {
        var globalConfig = new PipelineConfiguration { MaxDecompositionSubIssues = badValue };
        var templates = new List<PipelineJobTemplate>();
        var providerConfigs = new List<ProviderConfig>();

        var result = await PipelineConfigurationResolver.ResolveAsync(
            ct => Task.FromResult(globalConfig),
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            null,
            "repo-1",
            providerConfigs,
            CancellationToken.None);

        result.MaxDecompositionSubIssues.Should().BeGreaterThanOrEqualTo(1,
            $"a stored value of {badValue} must be clamped to at least 1 before reaching CreateSubIssuesStep");
    }

    /// <summary>
    /// Pre-loaded overload of ResolveAsync must also clamp MaxDecompositionSubIssues.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task ResolveAsync_PreLoaded_WhenGlobalConfigHasNonPositiveMaxDecompositionSubIssues_ClampsToOne(int badValue)
    {
        var preLoadedConfig = new PipelineConfiguration { MaxDecompositionSubIssues = badValue };
        var templates = new List<PipelineJobTemplate>();
        var providerConfigs = new List<ProviderConfig>();

        var result = await PipelineConfigurationResolver.ResolveAsync(
            preLoadedConfig,
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            null,
            "repo-1",
            providerConfigs,
            CancellationToken.None);

        result.MaxDecompositionSubIssues.Should().BeGreaterThanOrEqualTo(1,
            $"a pre-loaded value of {badValue} must be clamped to at least 1 before reaching CreateSubIssuesStep");
    }

    /// <summary>
    /// A valid MaxDecompositionSubIssues value must pass through the resolver unchanged.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task ResolveAsync_WhenGlobalConfigHasValidMaxDecompositionSubIssues_PreservesValue(int validValue)
    {
        var globalConfig = new PipelineConfiguration { MaxDecompositionSubIssues = validValue };
        var templates = new List<PipelineJobTemplate>();
        var providerConfigs = new List<ProviderConfig>();

        var result = await PipelineConfigurationResolver.ResolveAsync(
            ct => Task.FromResult(globalConfig),
            ct => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(templates),
            null,
            "repo-1",
            providerConfigs,
            CancellationToken.None);

        result.MaxDecompositionSubIssues.Should().Be(validValue,
            "a valid value must not be altered by the clamp");
    }
}
