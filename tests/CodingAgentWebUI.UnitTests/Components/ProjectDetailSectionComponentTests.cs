using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for ProjectDetailSection — steering textarea rendering and save.
/// </summary>
public class ProjectDetailSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public ProjectDetailSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        SetupDefaults();

        Services.AddSingleton(new ProjectChangeNotifier());
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void SteeringTextarea_RendersInSettingsTab()
    {
        var project = new PipelineProject { Id = "p1", Name = "Test" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // Click Settings tab
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        Assert.Contains("Steering Instructions", cut.Markup);
        Assert.Contains("These instructions are provided to every agent working on issues in this project", cut.Markup);
    }

    [Fact]
    public void SteeringTextarea_ShowsExistingContent()
    {
        var project = new PipelineProject { Id = "p1", Name = "Test", SteeringContent = "Use tabs not spaces" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        var textarea = cut.FindAll("textarea").First(t => t.TextContent.Contains("Use tabs not spaces") ||
            t.GetAttribute("value")?.Contains("Use tabs not spaces") == true ||
            t.InnerHtml.Contains("Use tabs not spaces"));
        Assert.NotNull(textarea);
    }

    [Fact]
    public async Task SteeringTextarea_SavePersistsContent()
    {
        var project = new PipelineProject { Id = "p1", Name = "Test" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        PipelineProject? savedProject = null;
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        // Find the steering textarea (last textarea in the settings tab area)
        var textareas = cut.FindAll("textarea");
        var steeringTextarea = textareas.Last();
        steeringTextarea.Change("My steering content");

        // Click Save Settings
        cut.Find(".btn-save").Click();

        Assert.NotNull(savedProject);
        Assert.Equal("My steering content", savedProject!.SteeringContent);
    }

    [Fact]
    public async Task SteeringTextarea_WhitespaceOnlySavesAsNull()
    {
        var project = new PipelineProject { Id = "p1", Name = "Test" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        PipelineProject? savedProject = null;
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        var textareas = cut.FindAll("textarea");
        var steeringTextarea = textareas.Last();
        steeringTextarea.Change("   \n  \t  ");

        cut.Find(".btn-save").Click();

        Assert.NotNull(savedProject);
        Assert.Null(savedProject!.SteeringContent);
    }

    [Fact]
    public async Task SteeringTextarea_EmptySavesAsNull()
    {
        var project = new PipelineProject { Id = "p1", Name = "Test", SteeringContent = "old content" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        PipelineProject? savedProject = null;
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        var textareas = cut.FindAll("textarea");
        var steeringTextarea = textareas.Last();
        steeringTextarea.Change("");

        cut.Find(".btn-save").Click();

        Assert.NotNull(savedProject);
        Assert.Null(savedProject!.SteeringContent);
    }

    [Fact]
    public void SteeringTextarea_ShowsPlaceholder()
    {
        var project = new PipelineProject { Id = "p1", Name = "Test" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        // Placeholder contains example content
        Assert.Contains("Code Style", cut.Markup);
    }
}

/// <summary>
/// bUnit component tests for ProjectDetailSection — Templates tab dropdown and add/move behavior.
/// </summary>
public class ProjectDetailSectionTemplatesTabTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public ProjectDetailSectionTemplatesTabTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        SetupDefaults();

        Services.AddSingleton(new ProjectChangeNotifier());
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void TemplatesDropdown_ShowsTemplatesNotInCurrentProject()
    {
        // Project A has T1, T2. Project B has T3. Viewing Project A.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1", "t2"] };
        var projectB = new PipelineProject { Id = "pB", Name = "Project B", TemplateIds = ["t3"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" },
            new() { Id = "t2", Name = "Template Two", IssueProviderId = "ip1", RepoProviderId = "rp1" },
            new() { Id = "t3", Name = "Template Three", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // Click Templates tab
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        // The dropdown should show only Template Three (from project B)
        var addSelect = cut.Find(".template-add-row select");
        var options = addSelect.QuerySelectorAll("option");

        // First option is placeholder, second should be Template Three
        Assert.Equal(2, options.Length);
        Assert.Contains("Template Three", options[1].TextContent);
        Assert.DoesNotContain("Template One", addSelect.InnerHtml);
        Assert.DoesNotContain("Template Two", addSelect.InnerHtml);
    }

    [Fact]
    public void TemplatesDropdown_ShowsAllTemplatesWhenProjectHasNone()
    {
        // Project A has no templates. Project B has T1. Viewing Project A.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = [] };
        var projectB = new PipelineProject { Id = "pB", Name = "Project B", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        var options = addSelect.QuerySelectorAll("option");

        // Placeholder + Template One
        Assert.Equal(2, options.Length);
        Assert.Contains("Template One", options[1].TextContent);
    }

    // TODO: This method (and AddTemplate_ReloadsDataAfterMove, AddTemplate_WhenSourceProjectNotFound_ShowsError)
    // is declared async Task but does not use await. The bUnit .Click() method is synchronous and internally
    // processes async handlers, so the tests work correctly, but the async modifier creates CS1998 warnings.
    // Consider removing the async modifier or restructuring to use await.
    [Fact]
    public async Task AddTemplate_CallsMoveTemplateAsyncWithCorrectSourceAndTarget()
    {
        // Project A viewing, Project B has T3. Select T3, click Add.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var projectB = new PipelineProject { Id = "pB", Name = "Project B", TemplateIds = ["t3"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" },
            new() { Id = "t3", Name = "Template Three", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // Click Templates tab
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        // Select T3 in the add dropdown
        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");

        // Click Add button
        cut.Find(".template-add-row .btn-save").Click();

        // Verify MoveTemplateAsync was called with source=pB, target=pA, templateId=t3
        _mockStore.Verify(s => s.MoveTemplateAsync("pB", "pA", "t3", It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: This test verifies implementation details (that internal load methods are called Times.AtLeast(2))
    // rather than observable UI behavior. Consider updating mock return values after the move and asserting
    // the rendered dropdown/list content changed. Also, Times.AtLeast(2) is overly weak — Times.Exactly(2)
    // would be more precise if the expected count is deterministic.
    [Fact]
    public async Task AddTemplate_ReloadsDataAfterMove()
    {
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var projectB = new PipelineProject { Id = "pB", Name = "Project B", TemplateIds = ["t3"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" },
            new() { Id = "t3", Name = "Template Three", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");
        cut.Find(".template-add-row .btn-save").Click();

        // After AddTemplate, LoadDataAsync is called which re-invokes these:
        // Initial render calls each once, AddTemplate triggers a second call
        _mockStore.Verify(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _mockStore.Verify(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _mockStore.Verify(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task AddTemplate_WhenSourceProjectNotFound_ShowsError()
    {
        // Template T3 exists in _allTemplates but is NOT in any project's TemplateIds
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" },
            new() { Id = "t3", Name = "Template Three", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA }); // Only project A, which doesn't have T3
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        (string Message, bool IsError)? statusMessage = null;
        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, msg => { statusMessage = msg; })));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");
        cut.Find(".template-add-row .btn-save").Click();

        // MoveTemplateAsync should NOT be called
        _mockStore.Verify(s => s.MoveTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Error status should be shown
        Assert.NotNull(statusMessage);
        Assert.True(statusMessage!.Value.IsError);
        Assert.Contains("not found", statusMessage.Value.Message);
    }
}
