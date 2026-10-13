using AwesomeAssertions;
using CodingAgent.Api.Consolidation;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Consolidation;

/// <summary>
/// Spec 051 2.4b: what the Consolidation page shows, worked out in the API from the stored templates,
/// projects and settings. UiConsolidationEndpointTests cover access and the trigger.
/// </summary>
public sealed class ConsolidationPageServiceTests
{
    private static PipelineJobTemplate Template(string id, string? brain = "brain-1", string? repo = "repo-1", string? issues = "issues-1",
        bool enabled = true, bool brainReadOnly = false) => new()
        {
            Id = id, Name = $"{id}-name", BrainProviderId = brain, RepoProviderId = repo ?? "", IssueProviderId = issues ?? "",
            Enabled = enabled, BrainReadOnly = brainReadOnly,
        };

    private static PipelineProject Project(string name, bool enabled = true, bool? brainReadOnly = null, params string[] templateIds) => new()
    {
        Id = $"{name}-id", Name = name, Enabled = enabled, BrainReadOnly = brainReadOnly, TemplateIds = templateIds,
    };

    [Fact]
    public void Overview_ListsTheEnabledTemplatesOfEnabledProjects_ByProjectName()
    {
        var projects = new[]
        {
            Project("zulu", templateIds: ["z1"]),
            Project("alpha", templateIds: ["a1", "a2", "missing"]),
            Project("off", enabled: false, templateIds: ["o1"]),
        };
        var templates = new[] { Template("z1"), Template("a1"), Template("a2", enabled: false), Template("o1") };

        var overview = ConsolidationPageService.BuildOverview(projects, templates, new PipelineConfiguration(), null);

        overview.Templates.Select(t => t.Id).Should().Equal("a1", "z1");
    }

    [Fact]
    public void Overview_SaysWhatEachTemplateSupports()
    {
        var projects = new[] { Project("alpha", templateIds: ["full", "no-brain", "no-issues"]) };
        var templates = new[] { Template("full"), Template("no-brain", brain: null), Template("no-issues", issues: null) };

        var overview = ConsolidationPageService.BuildOverview(projects, templates, new PipelineConfiguration(), null);

        overview.Templates.Should().Equal(
            new UiConsolidationTemplate("full", "full-name", true, true, false),
            new UiConsolidationTemplate("no-brain", "no-brain-name", false, true, false),
            new UiConsolidationTemplate("no-issues", "no-issues-name", true, false, false));
    }

    [Fact]
    public void Overview_BrainIsReadOnly_ByTheTemplatesFlag_OrTheGlobalSettingWithTheProjectsOverride()
    {
        var projects = new[]
        {
            Project("alpha", templateIds: ["own-flag", "global"]),
            Project("beta", brainReadOnly: false, templateIds: ["project-writes"]),
        };
        var templates = new[] { Template("own-flag", brainReadOnly: true), Template("global"), Template("project-writes") };
        var readOnlyGlobally = new PipelineConfiguration { BrainReadOnly = true };

        var overview = ConsolidationPageService.BuildOverview(projects, templates, readOnlyGlobally, null);

        overview.Templates.ToDictionary(t => t.Id, t => t.BrainReadOnly).Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["own-flag"] = true, ["global"] = true, ["project-writes"] = false,
        });
    }

    [Fact]
    public void Overview_MapsEveryTemplateWithABrain_AndPassesTheSuggestionsThrough()
    {
        var suggestions = new HarnessSuggestions { GeneratedAtUtc = DateTime.UtcNow, BasedOnRunCount = 4, SuccessRate = 0.5m, Suggestions = [] };
        var templates = new[] { Template("enabled"), Template("disabled", enabled: false, brain: "brain-2"), Template("brainless", brain: null) };

        var overview = ConsolidationPageService.BuildOverview([], templates, new PipelineConfiguration(), suggestions);

        overview.BrainProviderByTemplate.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["enabled"] = "brain-1", ["disabled"] = "brain-2",
        });
        overview.HarnessSuggestions.Should().BeSameAs(suggestions);
    }

    [Fact]
    public void RefactoringScan_WithoutAProject_UsesTheGlobalSettings()
    {
        var config = new PipelineConfiguration { MaxRefactoringProposals = 5, RefactoringReviewEnabled = false, HotspotAnalysisLookback = TimeSpan.FromDays(30) };

        ConsolidationPageService.BuildRefactoringScanSettings(config, null)
            .Should().Be(new UiRefactoringScanSettings(5, false, 30, false, false));
    }

    [Fact]
    public void RefactoringScan_MarksAProjectOverride_OnlyWhenItChangesTheValue()
    {
        var config = new PipelineConfiguration { MaxRefactoringProposals = 3, RefactoringReviewEnabled = true };
        var overriding = new PipelineProject { Id = "p", Name = "p", MaxRefactoringProposals = 7, RefactoringReviewEnabled = true };

        ConsolidationPageService.BuildRefactoringScanSettings(config, overriding)
            .Should().Be(new UiRefactoringScanSettings(7, true, 90, true, false));
    }
}
