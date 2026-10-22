using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.Auth;
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
}
