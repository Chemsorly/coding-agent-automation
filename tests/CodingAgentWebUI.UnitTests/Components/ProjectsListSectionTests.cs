using Bunit;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for ProjectsListSection.
/// Covers rendering, add/delete/toggle flows, and default project constraints.
/// </summary>
public class ProjectsListSectionTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public ProjectsListSectionTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockStore.Setup(s => s.DeleteProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ═══ Rendering ═══

    [Fact]
    public void RendersProjectList_WithProjectNames()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true },
            new() { Id = "proj-1", Name = "My Project", Enabled = true },
            new() { Id = "proj-2", Name = "Another Project", Enabled = false }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        Assert.Contains("Default", cut.Markup);
        Assert.Contains("My Project", cut.Markup);
        Assert.Contains("Another Project", cut.Markup);
    }

    [Fact]
    public void DefaultProject_ShowsLockIconAndBadge()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        Assert.Contains("🔒", cut.Markup);
        Assert.Contains("badge-default", cut.Markup);
    }

    [Fact]
    public void DefaultProject_DeleteButtonIsDisabled()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        var deleteBtn = cut.FindAll("button.btn-delete").First();
        Assert.NotNull(deleteBtn.GetAttribute("disabled"));
    }

    [Fact]
    public async Task NonDefaultProject_DeleteFlow_CallsDeleteProjectAsync()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true },
            new() { Id = "proj-1", Name = "Deletable", Enabled = true }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        // Click Delete on the non-default project (second delete button)
        var deleteButtons = cut.FindAll("button.btn-delete");
        var nonDefaultDeleteBtn = deleteButtons[1]; // Second row's delete button
        await nonDefaultDeleteBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Confirmation dialog should appear
        Assert.Contains("projects-confirm-overlay", cut.Markup);
        Assert.Contains("Deletable", cut.Markup);

        // Confirm deletion
        var confirmBtn = cut.Find(".projects-confirm-dialog button.btn-delete");
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ Add Project ═══

    [Fact]
    public async Task AddProject_NavigatesToDetail_WithoutSaving()
    {
        string? navigatedId = null;
        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback.Factory.Create<string>(this, v => navigatedId = v))
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Project"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Should have navigated with a valid GUID
        Assert.NotNull(navigatedId);
        Assert.True(Guid.TryParse(navigatedId, out _));

        // Should NOT have called SaveProjectAsync
        _mockStore.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══ Enabled Toggle ═══

    [Fact]
    public async Task EnabledToggle_CallsSaveProjectAsync()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = "proj-1", Name = "Test Project", Enabled = true }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        var checkbox = cut.Find("input[type='checkbox']");
        await checkbox.ChangeAsync(new ChangeEventArgs { Value = false });

        _mockStore.Verify(s => s.SaveProjectAsync(
            It.Is<PipelineProject>(p => p.Id == "proj-1" && p.Enabled == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ Epic Icon ═══

    [Fact]
    public void EpicIcon_ShownWhenEpicIssueProviderIdSet()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = "proj-1", Name = "Epic Project", Enabled = true, EpicIssueProviderId = "provider-1" }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        Assert.Contains("🧩", cut.Markup);
    }

    [Fact]
    public void EpicIcon_NotShownWhenEpicIssueProviderIdNull()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = "proj-1", Name = "Regular Project", Enabled = true, EpicIssueProviderId = null }
        };
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        var cut = Render<ProjectsListSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnNavigateToProject, EventCallback<string>.Empty)
            .Add(s => s.OnShowStatus, EventCallback<(string, bool)>.Empty));

        Assert.DoesNotContain("🧩", cut.Markup);
    }
}
