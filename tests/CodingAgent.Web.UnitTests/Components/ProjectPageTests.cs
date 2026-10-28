using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.Auth;
using CodingAgent.Web.UnitTests.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for ProjectPage — the project settings page at /projects/{id}.
/// </summary>
public class ProjectPageTests : BunitContext
{
    private const string TestProjectId = "p-1";

    private readonly Mock<IUiSettingsClient> _configClient = new();

    public ProjectPageTests()
    {
        _configClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _configClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _configClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _configClient.Setup(c => c.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Services.AddSingleton(_configClient.Object);
        Services.AddSingleton(Mock.Of<IUiProviderClient>(c =>
            c.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<ProviderConfig>>(new List<ProviderConfig>())));
        Services.AddSingleton(ProjectPageClients.UiConfig().Object);
    }

    [Fact]
    public void UserWithoutProjectAccess_ShowsNotFoundState()
    {
        var project = new PipelineProject { Id = TestProjectId, Name = "Test" };
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        // User has access to a different project only
        Services.AddTestAccess(TestAccess.Scoped(("other-project", AccessRole.Operator)));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        Assert.Contains("Project not found", cut.Markup);
    }

    [Theory]
    [InlineData(AccessRole.ReadOnly)]
    [InlineData(AccessRole.Operator)]
    public void ProjectReaderOrOperator_ShowsNotFoundState(AccessRole role)
    {
        // #3577: the project page is for admins on the project; the API refuses its reads to anyone else.
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject { Id = TestProjectId, Name = "Test" });
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, role)));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        Assert.Contains("Project not found", cut.Markup);
        _configClient.Verify(c => c.GetProjectByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ProjectAdmin_LoadsTemplatesFromThePipelinesPage_NotTheSettingsLists()
    {
        // #3577: the Settings lists (all templates, all projects, provider configs) are global admin only.
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject { Id = TestProjectId, Name = "Team Project", TemplateIds = ["t1"], EpicIssueProviderId = "epics" });
        var providers = new Mock<IUiProviderClient>();
        Services.AddSingleton(providers.Object);
        Services.AddSingleton(ProjectPageClients.UiConfig(
            templates: [new PipelineJobTemplate { Id = "t1", Name = "Team repo", IssueProviderId = "ip", RepoProviderId = "rp" }],
            issueProviders: [new ProviderConfig { Id = "epics", DisplayName = "Team epics", Kind = ProviderKind.Issue, ProviderType = "github" }]).Object);
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Admin)));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        Assert.Contains("Team epics", cut.Markup);
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();
        Assert.Contains("Team repo", cut.Find(".template-list").TextContent);
        _configClient.Verify(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _configClient.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Never);
        providers.Verify(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GlobalAdmin_CanSeeProject()
    {
        var project = new PipelineProject { Id = TestProjectId, Name = "My Project" };
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        Assert.Contains("My Project", cut.Markup);
        Assert.DoesNotContain("Project not found", cut.Markup);
    }

    [Fact]
    public void ProjectAdmin_CanSeeProject()
    {
        var project = new PipelineProject { Id = TestProjectId, Name = "Team Project" };
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Admin)));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        Assert.Contains("Team Project", cut.Markup);
        Assert.DoesNotContain("Project not found", cut.Markup);
    }

    [Fact]
    public void ProjectAdmin_CanSeeOverviewSaveButton_ButNotEnabledCheckbox()
    {
        var project = new PipelineProject { Id = TestProjectId, Name = "Team Project", Enabled = true };
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Admin)));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        // Save button visible for project admin in Overview tab
        Assert.NotNull(cut.Find("button.btn-save"));
        // Enabled checkbox is disabled (not editable by project admin).
        // Use the label text as a stable selector rather than positional [0], which could
        // match an override checkbox from a different tab fragment.
        var enabledLabel = cut.FindAll("label").FirstOrDefault(l => l.TextContent.Trim() == "Enabled");
        Assert.NotNull(enabledLabel);
        var enabledCheckbox = enabledLabel!.QuerySelector("input[type='checkbox']");
        Assert.NotNull(enabledCheckbox);
        Assert.True(enabledCheckbox!.HasAttribute("disabled"), "Enabled checkbox should be disabled for project admin");
    }

    [Fact]
    public async Task OnProjectSaved_ShowsStatusFeedback()
    {
        // Before the fix, HandleProjectSaved() was an empty method: firing OnProjectSaved produced no
        // visible status in the UI. This test reproduces that bug (fails against the empty body) and
        // serves as a permanent regression guard after the fix.
        var project = new PipelineProject { Id = TestProjectId, Name = "My Project" };
        _configClient.Setup(c => c.GetProjectByIdAsync(TestProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));

        var cut = Render<ProjectPage>(p => p.Add(x => x.ProjectId, TestProjectId));

        // No status shown before OnProjectSaved fires
        Assert.Empty(cut.FindAll(".inline-status"));

        // Fire OnProjectSaved directly on the child component instance — this calls HandleProjectSaved
        // on ProjectPage without triggering any save method or Guard.DemandAsync in ProjectDetailSection.
        var section = cut.FindComponent<ProjectDetailSection>();
        await cut.InvokeAsync(() => section.Instance.OnProjectSaved.InvokeAsync());

        // HandleProjectSaved must set a status message, causing InlineStatus to render its div.
        var statusDiv = cut.Find(".inline-status");
        Assert.NotEmpty(statusDiv.TextContent.Trim());
    }
}
