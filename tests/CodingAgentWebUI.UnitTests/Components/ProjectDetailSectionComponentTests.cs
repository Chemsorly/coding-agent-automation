using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

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
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
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
