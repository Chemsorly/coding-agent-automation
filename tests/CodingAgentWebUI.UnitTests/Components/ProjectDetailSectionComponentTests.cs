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
        var steeringTextarea = textareas[^1];
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
        var steeringTextarea = textareas[^1];
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
        var steeringTextarea = textareas[^1];
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
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
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
        _mockStore.Verify(s => s.MoveTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()), Times.Never);

        // Error status should be shown
        Assert.NotNull(statusMessage);
        Assert.True(statusMessage!.Value.IsError);
        Assert.Contains("not found", statusMessage.Value.Message);
    }

    [Fact]
    public async Task RemoveTemplate_RefreshesAllProjects()
    {
        // Project A has T1. After removal, LoadDataAsync must be called to refresh _allProjects.
        // TODO: The mock always returns the same projectA data (TemplateIds = ["t1"]) on every call,
        // so this test only verifies LoadProjectsAsync was called, not that the refreshed data was
        // applied to the rendered component. To properly validate the stale-data fix, the mock should
        // return updated data on the second call (e.g., projectA with TemplateIds = []), and the test
        // should assert the rendered template list is empty after removal. As written the test would
        // pass even if LoadDataAsync results were never applied to component state.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        // Click remove button for Template One
        cut.Find(".btn-danger").Click();

        // LoadProjectsAsync must be called at least twice:
        // once on initial render, once after RemoveTemplate → LoadDataAsync
        _mockStore.Verify(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task AddTemplate_PassesNonNoneCancellationToken()
    {
        // Verify the CancellationToken passed to MoveTemplateAsync has CanBeCanceled == true
        // (i.e., it comes from the component-scoped CTS, not CancellationToken.None).
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

        CancellationToken capturedToken = CancellationToken.None;
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, TemplateId, CancellationToken>((_, _, _, ct) => capturedToken = ct)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");
        cut.Find(".template-add-row .btn-save").Click();

        // The token must be cancellable (from the component CTS), not CancellationToken.None
        Assert.True(capturedToken.CanBeCanceled,
            "MoveTemplateAsync must receive a component-scoped cancellable token, not CancellationToken.None");
    }

    [Fact]
    public async Task AddTemplate_WhenOperationCancelled_DoesNotShowError()
    {
        // When MoveTemplateAsync throws OperationCanceledException, the error handler
        // must NOT be invoked — the OperationCanceledException is re-thrown, so the
        // catch (Exception ex) block that calls OnShowStatus is never reached.
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
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        (string Message, bool IsError)? statusMessage = null;
        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, msg => { statusMessage = msg; })));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();
        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");
        cut.Find(".template-add-row .btn-save").Click();

        // OnShowStatus must NOT have been called with an error — the OperationCanceledException
        // bypasses the generic catch block (it is re-thrown, not swallowed).
        // TODO: Assert.Null(statusMessage) passes vacuously if bUnit swallows the propagated
        // OperationCanceledException rather than letting it surface. Add a Moq Verify that
        // OnShowStatus was never invoked, or use InvokeAsync with Assert.ThrowsAsync to confirm
        // the exception propagates, rather than relying solely on the side-effect absence.
        Assert.Null(statusMessage);
    }
}

/// <summary>
/// bUnit component tests for ProjectDetailSection — MCP Servers tab rendering.
/// Verifies conditional rendering of the server table, form, and add-button
/// across all state combinations. These are characterization tests that lock in
/// current rendering behavior to prevent regressions during Extract Method refactoring.
/// </summary>
public class ProjectDetailSectionMcpTabTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public ProjectDetailSectionMcpTabTests()
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
    public void McpTab_WhenServersExist_RendersServerTable()
    {
        // Arrange: project with one MCP server
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            McpServers = [new McpServerConfig { Name = "context7", Type = "stdio", Command = "uvx" }]
        };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // Act: navigate to MCP Servers tab
        // TODO: Replace .First() with .Single() or assert count before calling .First() to produce
        //       a descriptive failure instead of an InvalidOperationException when the tab is absent.
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: server table is rendered with the server name
        // TODO: Strengthen assertions — check for data cell values (Type="stdio", Command="uvx",
        //       rendered "Command / URL" cell) rather than just the CSS class name "monitoring-table".
        //       The current assertion passes even if the table renders with completely wrong data.
        Assert.Contains("monitoring-table", cut.Markup);
        Assert.Contains("context7", cut.Markup);
        // TODO: Add a test covering the HTTP server branch (Type="http", Url="https://...") to verify
        //       the `server.Type == "http" ? server.Url : $"{server.Command} {server.Args}"` ternary
        //       in RenderMcpServerTable. A swap of the branches would not be caught by current tests.
    }

    [Fact]
    public void McpTab_WhenServersEmpty_DoesNotRenderServerTable()
    {
        // Arrange: project with no MCP servers
        // TODO: Consider also covering the empty-list branch with `McpServers = []` (non-null) to
        //       directly test the `_mcpServers.Count > 0` guard in RenderMcpServerTable without
        //       coupling the test to the component's null-to-empty-list initialisation path.
        var project = new PipelineProject { Id = "p1", Name = "Test", McpServers = null };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // Act: navigate to MCP Servers tab
        // TODO: Replace .First() with .Single() or assert count before calling .First() — see note
        //       in McpTab_WhenServersExist_RendersServerTable.
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: no server table; add button is visible instead
        Assert.DoesNotContain("monitoring-table", cut.Markup);
        Assert.Contains("Add MCP Server", cut.Markup);
    }

    [Fact]
    public void McpTab_WhenShowMcpFormTrue_RendersFormAndHidesAddButton()
    {
        // Arrange: project with no servers; we'll click the Add button to show the form
        var project = new PipelineProject { Id = "p1", Name = "Test" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // TODO: Replace .First() with .Single() or assert count before calling .First() — see note
        //       in McpTab_WhenServersExist_RendersServerTable.
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Act: click the Add MCP Server button to show the form
        cut.Find(".btn-add").Click();

        // Assert: form fields are rendered, add-button is no longer visible
        // TODO: Assert at least one form input field (e.g. Assert.Contains("Name *", cut.Markup))
        //       to verify the form body is rendered, not just the save/cancel buttons.
        //       Currently the test would pass even if the form fields were omitted by accident.
        Assert.Contains("btn-cancel", cut.Markup);
        Assert.Contains("Save Server", cut.Markup);
        Assert.DoesNotContain("btn-add", cut.Markup);
        // TODO: Add a test covering the HTTP-type branch of the form (set _mcpForm.Type = "http" and
        //       verify URL/Headers fields appear) to guard against accidental removal of the
        //       `@if (_mcpForm.Type == "http")` block in RenderMcpServerFormOrAddButton.
    }

    [Fact]
    public void McpTab_WhenShowMcpFormFalse_RendersAddButtonAndHidesForm()
    {
        // Arrange: project with no servers; form is not shown by default
        var project = new PipelineProject { Id = "p1", Name = "Test" };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigStore, _mockStore.Object));

        // Act: navigate to MCP Servers tab (form is false by default)
        // TODO: Replace .First() with .Single() or assert count before calling .First() — see note
        //       in McpTab_WhenServersExist_RendersServerTable.
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: add button is rendered, form save/cancel buttons are absent
        Assert.Contains("btn-add", cut.Markup);
        Assert.DoesNotContain("Save Server", cut.Markup);
        Assert.DoesNotContain("btn-cancel", cut.Markup);
    }

    // TODO: Add a test covering the "Edit" flow:
    //       - Render a project with at least one MCP server.
    //       - Navigate to the MCP Servers tab and click the Edit button for the first server.
    //       - Assert the form heading reads "Edit MCP Server" (not "Add").
    //       - Assert the form is pre-populated with the server's current values.
    //       - Assert that _editingMcpIndex is reflected correctly (form heading ternary).
    //       Without this test a regression in EditMcpServer() or the heading ternary would go
    //       undetected.
}
