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
/// The Pipelines page warns about templates saved before the binding rules were enforced
/// (<see cref="TemplateBindingRules"/>): a repository or an issue tracker used by two enabled templates,
/// or two enabled templates with the same name in one project.
/// </summary>
public class TemplateTableSectionBindingConflictTests : BunitContext
{
    public TemplateTableSectionBindingConflictTests()
    {
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(new Mock<ILogger>().Object));
    }

    private static PipelineJobTemplate Template(string id, string name, string repo, bool enabled = true) => new()
    {
        Id = id, Name = name, IssueProviderId = $"issues-{id}", RepoProviderId = repo, Enabled = enabled
    };

    private IRenderedComponent<TemplateTableSection> RenderSection(params PipelineJobTemplate[] templates) =>
        Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, templates.ToList())
            .Add(s => s.Projects, new List<PipelineProject>
            {
                new() { Id = "proj-1", Name = "Product", TemplateIds = templates.Select(t => t.Id).ToList() }
            })
            .Add(s => s.IssueProviders, [])
            .Add(s => s.RepoProviders, [])
            .Add(s => s.BrainProviders, [])
            .Add(s => s.PipelineProviders, [])
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>())
            .Add(s => s.QualityGateConfigs, [])
            .Add(s => s.ReviewerConfigs, [])
            .Add(s => s.AgentProfiles, [])
            .Add(s => s.PipelineConfig, new PipelineConfiguration()));

    [Fact]
    public void TwoEnabledTemplatesOnOneRepository_ShowTheWarning()
    {
        var cut = RenderSection(Template("t1", "Api", "repo-1"), Template("t2", "Api again", "repo-1"));

        var banner = cut.Find(".pipeline-warning-banner");
        banner.TextContent.Should().Contain("\"Api\"").And.Contain("\"Api again\"").And.Contain("same repository");
    }

    [Fact]
    public void ConflictWithADisabledTemplate_ShowsNoWarning()
    {
        var cut = RenderSection(Template("t1", "Api", "repo-1"), Template("t2", "Api again", "repo-1", enabled: false));

        cut.FindAll(".pipeline-warning-banner").Should().BeEmpty();
    }
}
