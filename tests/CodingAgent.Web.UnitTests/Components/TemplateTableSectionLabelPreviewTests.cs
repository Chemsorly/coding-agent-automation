using AwesomeAssertions;
using Bunit;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for <see cref="TemplateTableSection.GetLabelPreview"/>: verifies that the preview
/// shows the profile that dispatch would actually use (closest-fit / fewest labels), and that
/// quality gates and reviewers are resolved against the matched profile's labels — not the
/// full repository label set.
/// </summary>
public class TemplateTableSectionLabelPreviewTests : BunitContext
{
    private const string RepoProviderId = "rp-preview-1";
    private const string TemplateId = "t-preview-1";

    public TemplateTableSectionLabelPreviewTests()
    {
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(new Mock<ILogger>().Object));
    }

    private IRenderedComponent<TemplateTableSection> RenderSection(
        List<ProviderConfig> repoProviders,
        IReadOnlyList<AgentProfile> agentProfiles,
        IReadOnlyList<QualityGateConfiguration> qualityGateConfigs,
        PipelineConfiguration? pipelineConfig = null) =>
        Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, new List<PipelineJobTemplate>
            {
                new() { Id = TemplateId, Name = "Preview Template", IssueProviderId = "ip-prev-1", RepoProviderId = RepoProviderId, Enabled = true }
            })
            .Add(s => s.Projects, new List<PipelineProject>
            {
                new() { Id = "proj-prev-1", Name = "Project", TemplateIds = [TemplateId] }
            })
            .Add(s => s.IssueProviders, [new ProviderConfig { Id = "ip-prev-1", DisplayName = "Issues", Kind = ProviderKind.Issue, ProviderType = "GitHub" }])
            .Add(s => s.RepoProviders, repoProviders)
            .Add(s => s.BrainProviders, [])
            .Add(s => s.PipelineProviders, [])
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>())
            .Add(s => s.QualityGateConfigs, qualityGateConfigs)
            .Add(s => s.ReviewerConfigs, [])
            .Add(s => s.AgentProfiles, agentProfiles)
            .Add(s => s.PipelineConfig, pipelineConfig ?? new PipelineConfiguration()));

    /// <summary>
    /// A .NET-only repository (3 labels) should show the 3-label .NET profile in the preview,
    /// not the 5-label polyglot profile. Quality gates must be resolved against the .NET profile's
    /// labels, so the .NET QGC appears but the Python QGC does not.
    /// </summary>
    [Fact]
    public void GetLabelPreview_PolyglotAndDotNetProfiles_DotNetRepoShowsDotNetProfileAndQualityGates()
    {
        // ARRANGE
        var dotnetProfile = new AgentProfile
        {
            Id = "dotnet-profile",
            DisplayName = ".NET Profile",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };
        var polyglotProfile = new AgentProfile
        {
            Id = "polyglot-profile",
            DisplayName = "Polyglot Profile",
            AgentProviderConfigId = "agent-2",
            Enabled = true,
            MatchLabels = ["kiro", "dotnet", "dotnet10", "python", "python312"]
        };

        var dotnetQgc = new QualityGateConfiguration { DisplayName = ".NET", MatchLabels = ["dotnet"], Enabled = true };
        var pythonQgc = new QualityGateConfiguration { DisplayName = "Python", MatchLabels = ["python"], Enabled = true };

        var repoProvider = new ProviderConfig
        {
            Id = RepoProviderId,
            DisplayName = "Repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            RequiredLabels = ["kiro", "dotnet", "dotnet10"]
        };

        var cut = RenderSection(
            repoProviders: [repoProvider],
            agentProfiles: [dotnetProfile, polyglotProfile],
            qualityGateConfigs: [dotnetQgc, pythonQgc]);

        // ACT
        var result = cut.Instance.GetLabelPreview(RepoProviderId);

        // ASSERT: exactly one profile shown — the .NET profile (3 labels, not the 5-label polyglot)
        result.Profiles.Should().HaveCount(1, "only the closest-fit profile should be shown");
        result.Profiles.Should().Contain(".NET Profile", "the 3-label .NET profile covers the repo labels with fewer labels");

        // ASSERT: quality gates resolved against the .NET profile's labels
        result.QualityGates.Should().Contain(".NET",
            "dotnet label in the .NET profile's MatchLabels intersects with the .NET QGC");
        result.QualityGates.Should().NotContain("Python",
            "python is absent from the .NET profile's MatchLabels so the Python QGC must not appear");
    }

    /// <summary>
    /// When no profile covers the repository's required labels, <c>Profiles</c> is empty and
    /// quality gates fall back to the repository labels directly — mirroring the pre-profile-match
    /// behaviour and ensuring the fallback path is exercised.
    /// </summary>
    [Fact]
    public void GetLabelPreview_NoMatchingProfile_ProfilesEmptyAndQualityGatesFromRepoLabels()
    {
        // ARRANGE: only a Java profile, but the repo is labelled for .NET
        var javaProfile = new AgentProfile
        {
            Id = "java-profile",
            DisplayName = "Java Profile",
            AgentProviderConfigId = "agent-1",
            Enabled = true,
            MatchLabels = ["kiro", "java", "java21"]
        };

        var dotnetQgc = new QualityGateConfiguration { DisplayName = ".NET", MatchLabels = ["dotnet"], Enabled = true };
        var pythonQgc = new QualityGateConfiguration { DisplayName = "Python", MatchLabels = ["python"], Enabled = true };

        var repoProvider = new ProviderConfig
        {
            Id = RepoProviderId,
            DisplayName = "Repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            RequiredLabels = ["kiro", "dotnet", "dotnet10"]
        };

        var cut = RenderSection(
            repoProviders: [repoProvider],
            agentProfiles: [javaProfile],
            qualityGateConfigs: [dotnetQgc, pythonQgc]);

        // ACT
        var result = cut.Instance.GetLabelPreview(RepoProviderId);

        // ASSERT: no profile matched
        result.Profiles.Should().BeEmpty("the Java profile does not cover the .NET repo labels");

        // ASSERT: quality gates resolved against repo labels (fallback path)
        result.QualityGates.Should().Contain(".NET",
            "dotnet label in repo labels intersects with the .NET QGC when falling back to repo labels");
        result.QualityGates.Should().NotContain("Python",
            "python is absent from repo labels [kiro, dotnet, dotnet10] so the Python QGC must not appear");
    }
}
