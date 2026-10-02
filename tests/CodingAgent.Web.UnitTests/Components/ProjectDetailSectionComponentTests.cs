using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for ProjectDetailSection — steering textarea rendering and save.
/// </summary>
public class ProjectDetailSectionComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;

    public ProjectDetailSectionComponentTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        SetupDefaults();
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
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
            .Add(s => s.ConfigClient, _mockStore.Object));

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
            .Add(s => s.ConfigClient, _mockStore.Object));

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
            .Add(s => s.ConfigClient, _mockStore.Object));

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
            .Add(s => s.ConfigClient, _mockStore.Object));

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
            .Add(s => s.ConfigClient, _mockStore.Object));

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
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        // Placeholder contains example content
        Assert.Contains("Code Style", cut.Markup);
    }
}

/// <summary>
/// bUnit component tests for ProjectDetailSection — Settings tab numeric range validation.
/// Verifies that out-of-range int overrides are rejected before saving and an error status is shown.
/// </summary>
public class ProjectDetailSectionSettingsValidationTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;

    public ProjectDetailSectionSettingsValidationTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        SetupDefaults();
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Sets up a project, renders the component, navigates to Settings tab, overrides
    /// MaxDecompositionSubIssues input to an out-of-range value (999), and returns
    /// the rendered component along with a status message capture.
    /// </summary>
    private (IRenderedComponent<ProjectDetailSection> cut, Func<(string Message, bool IsError)?> getStatus)
        RenderWithOutOfRangeMaxDecompositionSubIssues()
    {
        // Start with an in-range value so the Override button becomes a visible input
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            MaxDecompositionSubIssues = 10  // valid (1-20)
        };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        (string Message, bool IsError)? capturedStatus = null;

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigClient, _mockStore.Object)
            .Add(s => s.OnShowStatus,
                EventCallback.Factory.Create<(string, bool)>(this, msg => capturedStatus = msg)));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        // Find the number input for "Max Sub-Issues Per Epic" and change it to 999 (out of range, max 20)
        var numberInputs = cut.FindAll("input[type='number']");
        // MaxDecompositionSubIssues is in the Decomposition section. Find by querying min/max attrs.
        // TODO: This selector is fragile — it matches the first input[type='number'] with min="1" max="20".
        // If any other Settings tab field is also constrained to min=1 max=20, the test will silently target
        // the wrong field. Replace with a stable selector (e.g. locate by label text "Max Sub-Issues Per Epic"
        // or add a data-testid attribute). (Review findings: Correctness:237, TestQualityReviewer:213)
        var subIssueInput = numberInputs.First(i =>
            i.GetAttribute("min") == "1" && i.GetAttribute("max") == "20");
        subIssueInput.Change(999);

        return (cut, () => capturedStatus);
    }

    [Fact]
    public void SaveSettings_RefusedByTheApi_ShowsTheReason()
    {
        // The API checks each override against the range of its setting and names the one it refuses.
        const string reason = "MaxDecompositionSubIssues must be between 1 and 20 (was 999).";
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(reason));
        var (cut, getStatus) = RenderWithOutOfRangeMaxDecompositionSubIssues();

        cut.Find(".btn-save").Click();

        var status = getStatus();
        Assert.NotNull(status);
        Assert.True(status!.Value.IsError, "the status shown must be an error");
        Assert.Contains(reason, status.Value.Message);
    }

    [Fact]
    public void SettingsTab_SteeringPlaceholder_ShowsLineBreaks()
    {
        // An "&#10;" entity in an attribute of a rendered element is not decoded, so it would show as text.
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject { Id = "p1", Name = "Test" });
        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigClient, _mockStore.Object));
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        var placeholder = cut.FindAll("textarea").Select(t => t.GetAttribute("placeholder") ?? "")
            .Single(p => p.StartsWith("## Code Style", StringComparison.Ordinal));

        Assert.Contains("\n- Use descriptive variable names\n", placeholder);
        Assert.Contains("## Build & Test", placeholder);
        Assert.DoesNotContain("&#", placeholder);
    }

    [Fact]
    public void SaveSettings_RefusedByTheApi_KeepsTheEditsWhenTheParentRerenders()
    {
        // Showing the status re-renders the parent, which sets this section's parameters again with the same project.
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("MaxDecompositionSubIssues must be between 1 and 20 (was 999)."));
        var (cut, _) = RenderWithOutOfRangeMaxDecompositionSubIssues();
        cut.Find(".btn-save").Click();

        cut.Render(p => p.Add(s => s.ProjectId, "p1"));

        Assert.Equal("999", cut.Find("[data-setting='MaxDecompositionSubIssues'] input").GetAttribute("value"));
        _mockStore.Verify(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SaveSettings_WithValidOverrides_SavesSuccessfully()
    {
        // Project with valid MaxDecompositionSubIssues already set
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            MaxDecompositionSubIssues = 10  // valid (1-20)
        };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        PipelineProject? savedProject = null;
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigClient, _mockStore.Object));

        // TODO: This test does not navigate to the Settings tab before clicking .btn-save, unlike the two
        // out-of-range tests which call RenderWithOutOfRangeMaxDecompositionSubIssues() and explicitly click
        // the Settings tab. If .btn-save is only rendered/active when the Settings tab is selected, this test
        // may be clicking the wrong button (or nothing) and producing a false pass. Add:
        //   cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();
        // before the save click to mirror the real user flow. (Review finding: TestQualityReviewer:268)
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Settings")).Click();

        // Keep value at 10 (valid, 1-20) — just click save
        cut.Find(".btn-save").Click();

        _mockStore.Verify(
            s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "SaveProjectAsync must be called exactly once for valid overrides");
        Assert.NotNull(savedProject);
        Assert.Equal(10, savedProject!.MaxDecompositionSubIssues);
    }
}

/// <summary>
/// bUnit component tests for ProjectDetailSection — Templates tab dropdown and add/move behavior.
/// </summary>
public class ProjectDetailSectionTemplatesTabTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;

    public ProjectDetailSectionTemplatesTabTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        SetupDefaults();
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        var options = addSelect.QuerySelectorAll("option");

        // Placeholder + Template One
        Assert.Equal(2, options.Length);
        Assert.Contains("Template One", options[1].TextContent);
    }

    [Fact]
    public void TemplatesTab_ListsByName_WithoutReorderButtons()
    {
        // Templates are ordered by name, so the tab has no manual order to change.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1", "t2"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Api", IssueProviderId = "ip1", RepoProviderId = "rp1" },
            new() { Id = "t2", Name = "Web", IssueProviderId = "ip2", RepoProviderId = "rp2" }
        };
        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        Assert.Equal(["Api", "Web"], cut.FindAll(".template-row .template-name").Select(e => e.TextContent));
        Assert.Empty(cut.FindAll("button[title='Move up'], button[title='Move down']"));
        Assert.Contains("Listed by name", cut.Markup);
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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

        // Click Templates tab
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        // Select T3 in the add dropdown
        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");

        // Click Add button
        cut.Find(".template-add-row .btn-save").Click();

        // Verify MoveTemplateAsync was called with source=pB, target=pA, templateId=t3
        _mockStore.Verify(s => s.MoveTemplateAsync(new ProjectId("pB"), new ProjectId("pA"), "t3", It.IsAny<CancellationToken>()), Times.Once);
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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");
        cut.Find(".template-add-row .btn-save").Click();

        // After AddTemplate, LoadDataAsync is called which re-invokes these:
        // Initial render calls each once, AddTemplate triggers a second call
        _mockStore.Verify(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _mockStore.Verify(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _mockStore.Verify(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA }); // Only project A, which doesn't have T3
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        (string Message, bool IsError)? statusMessage = null;
        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, msg => { statusMessage = msg; })));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        var addSelect = cut.Find(".template-add-row select");
        addSelect.Change("t3");
        cut.Find(".template-add-row .btn-save").Click();

        // MoveTemplateAsync should NOT be called
        _mockStore.Verify(s => s.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Error status should be shown
        Assert.NotNull(statusMessage);
        Assert.True(statusMessage!.Value.IsError);
        Assert.Contains("not found", statusMessage.Value.Message);
    }

    [Fact]
    public async Task RemoveTemplate_NonDefaultProject_CallsMoveTemplateAsyncToDefault()
    {
        // Clicking ✕ on a non-Default project template must call MoveTemplateAsync to Default,
        // NOT SaveProjectAsync.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        await cut.InvokeAsync(() => cut.Find(".btn-icon-danger").Click());

        // MoveTemplateAsync must be called with (pA, DefaultProjectId, "t1")
        _mockStore.Verify(s => s.MoveTemplateAsync(
            new ProjectId("pA"),
            new ProjectId(WellKnownIds.DefaultProjectId),
            "t1",
            It.IsAny<CancellationToken>()), Times.Once);

        // SaveProjectAsync must NOT be called for the removal
        _mockStore.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveTemplate_DefaultProject_XButtonIsHidden()
    {
        // The ✕ button must not be rendered when the current project is Default.
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = ["t1"]
        };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultProject);
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { defaultProject });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, WellKnownIds.DefaultProjectId)
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        // No danger button (✕) should be rendered in the Default project
        var dangerButtons = cut.FindAll(".btn-icon-danger");
        Assert.Empty(dangerButtons);
    }

    [Fact]
    public async Task RemoveTemplate_MoveTemplateAsyncFails_ShowsErrorStatus()
    {
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network error"));

        (string Message, bool IsError)? statusMessage = null;
        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, msg => { statusMessage = msg; })));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        await cut.InvokeAsync(() => cut.Find(".btn-icon-danger").Click());

        Assert.NotNull(statusMessage);
        Assert.True(statusMessage!.Value.IsError);
        Assert.Contains("network error", statusMessage.Value.Message);
    }

    [Fact]
    public async Task RemoveTemplate_PassesCancellableToken()
    {
        // Verify the CancellationToken passed to MoveTemplateAsync is cancellable
        // (from the component-scoped CTS, not CancellationToken.None).
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        CancellationToken capturedToken = CancellationToken.None;
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectId, ProjectId, string, CancellationToken>((_, _, _, ct) => capturedToken = ct)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        await cut.InvokeAsync(() => cut.Find(".btn-icon-danger").Click());

        Assert.True(capturedToken.CanBeCanceled,
            "RemoveTemplate must pass a component-scoped cancellable token, not CancellationToken.None");
    }

    [Fact]
    public async Task RemoveTemplate_ReloadsDataAfterMove()
    {
        // After MoveTemplateAsync succeeds, LoadDataAsync must be called to refresh the component state.
        var projectA = new PipelineProject { Id = "pA", Name = "Project A", TemplateIds = ["t1"] };
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template One", IssueProviderId = "ip1", RepoProviderId = "rp1" }
        };

        _mockStore.Setup(s => s.GetProjectByIdAsync("pA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectA);
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("Templates")).Click();

        await cut.InvokeAsync(() => cut.Find(".btn-icon-danger").Click());

        // After RemoveTemplate → LoadDataAsync, data-loading methods must have been called twice
        _mockStore.Verify(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _mockStore.Verify(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));

        // MoveTemplateAsync must have been called exactly once (paired with reload verification)
        _mockStore.Verify(s => s.MoveTemplateAsync(
            It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // TODO [WARNING]: Stale-mock problem — the mocks for GetProjectsAsync and GetAllTemplatesAsync
        // always return the original data (TemplateIds = ["t1"]) on every call, so the post-reload
        // render still shows the template. This test only verifies that the methods were called at
        // least twice; it cannot detect a bug where LoadDataAsync results are never applied to
        // component state. To make this test meaningful: return updated data (projectA with
        // TemplateIds = [] and empty template list) on the second mock call and assert the
        // rendered template list is empty after the reload.
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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        CancellationToken capturedToken = CancellationToken.None;
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectId, ProjectId, string, CancellationToken>((_, _, _, ct) => capturedToken = ct)
            .Returns(Task.CompletedTask);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object));

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
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { projectA, projectB });
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
        _mockStore.Setup(s => s.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        (string Message, bool IsError)? statusMessage = null;
        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "pA")
            .Add(s => s.ConfigClient, _mockStore.Object)
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
    private readonly Mock<IPipelineApiConfigClient> _mockStore;

    public ProjectDetailSectionMcpTabTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        SetupDefaults();
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
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
            .Add(s => s.ConfigClient, _mockStore.Object));

        // Act: navigate to MCP Servers tab
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: server table is rendered with the server name
        Assert.Contains("monitoring-table", cut.Markup);
        Assert.Contains("context7", cut.Markup);
    }

    [Fact]
    public void McpTab_WhenServersEmpty_DoesNotRenderServerTable()
    {
        // Arrange: project with no MCP servers
        var project = new PipelineProject { Id = "p1", Name = "Test", McpServers = null };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigClient, _mockStore.Object));

        // Act: navigate to MCP Servers tab
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
            .Add(s => s.ConfigClient, _mockStore.Object));

        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Act: click the Add MCP Server button to show the form
        cut.Find(".btn-add").Click();

        // Assert: form is rendered, add-button is no longer visible
        Assert.Contains("btn-cancel", cut.Markup);
        Assert.Contains("Save Server", cut.Markup);
        Assert.DoesNotContain("btn-add", cut.Markup);
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
            .Add(s => s.ConfigClient, _mockStore.Object));

        // Act: navigate to MCP Servers tab (form is false by default)
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: add button is rendered, form save/cancel buttons are absent
        Assert.Contains("btn-add", cut.Markup);
        Assert.DoesNotContain("Save Server", cut.Markup);
        Assert.DoesNotContain("btn-cancel", cut.Markup);
    }

    /// <summary>
    /// Regression test: NullReferenceException when McpServerConfig.Headers is null.
    /// Null arises when a MessagePack payload produced by an older binary (lacking the Headers member)
    /// is deserialized by ContractlessStandardResolverAllowPrivate — the property default initializer
    /// is not invoked for absent members by the Contractless resolver.
    /// </summary>
    [Fact]
    public void McpTab_WhenServerHasNullHeaders_RendersWithoutException()
    {
        // Arrange: McpServerConfig with Headers forced to null via nullable suppression,
        // simulating a MessagePack payload from an older binary that lacked the Headers member.
        var serverWithNullHeaders = new McpServerConfig
        {
            Name = "test-server",
            Type = "http",
            Command = null
        } with
        { Headers = null! };

        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            McpServers = [serverWithNullHeaders]
        };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigClient, _mockStore.Object));

        // Act: navigate to MCP Servers tab — this triggered NullReferenceException before the fix
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: component renders without throwing and shows the server name
        // TODO [WARNING]: This assertion is weak — it does not verify that the Headers field
        // maps to an empty string. If the fix were reverted but an exception were suppressed
        // upstream, this assertion would still pass. Consider asserting that the rendered
        // MCP row contains no "key=value" text for headers (e.g. check a headers cell is empty).
        Assert.Contains("test-server", cut.Markup);
    }

    /// <summary>
    /// Regression test: NullReferenceException when McpServerConfig.Env is null.
    /// Env was added as Key(5) before Headers (Key(8)); the same Contractless MessagePack
    /// resolver behaviour applies for payloads predating the Env member.
    /// </summary>
    [Fact]
    public void McpTab_WhenServerHasNullEnv_RendersWithoutException()
    {
        // Arrange: McpServerConfig with Env forced to null via nullable suppression.
        var serverWithNullEnv = new McpServerConfig
        {
            Name = "env-null-server",
            Type = "stdio",
            Command = "uvx"
        } with
        { Env = null! };

        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            McpServers = [serverWithNullEnv]
        };
        _mockStore.Setup(s => s.GetProjectByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p1")
            .Add(s => s.ConfigClient, _mockStore.Object));

        // Act: navigate to MCP Servers tab — this triggered NullReferenceException before the fix
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains("MCP Servers")).Click();

        // Assert: component renders without throwing and shows the server name
        // TODO [WARNING]: This assertion is weak — it does not verify that the EnvVars field
        // maps to an empty string. If the fix were reverted but an exception were suppressed
        // upstream, this assertion would still pass. Consider asserting that the rendered
        // MCP row contains no "key=value" text for env vars (e.g. check an env vars cell is empty).
        Assert.Contains("env-null-server", cut.Markup);
    }

    // ── Project review ────────────────────────────────────────────────────────

    private IRenderedComponent<ProjectDetailSection> RenderProject(PipelineProject project, Action<PipelineProject>? onSave = null)
    {
        _mockStore.Setup(s => s.GetProjectByIdAsync(project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _mockStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineProject, CancellationToken>((p, _) => onSave?.Invoke(p))
            .Returns(Task.CompletedTask);
        return Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, project.Id)
            .Add(s => s.ConfigClient, _mockStore.Object));
    }

    private static void OpenTab(IRenderedComponent<ProjectDetailSection> cut, string name) =>
        cut.FindAll(".tab-btn").First(b => b.TextContent.Contains(name)).Click();

    private static void ClickButton(IRenderedComponent<ProjectDetailSection> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text)).Click();

    [Fact]
    public void ProjectReviewTab_DefaultProject_IsNotOffered()
    {
        var cut = RenderProject(new PipelineProject { Id = WellKnownIds.DefaultProjectId, Name = "Default" });

        Assert.DoesNotContain(cut.FindAll(".tab-btn"), b => b.TextContent.Contains("Project Review"));
    }

    [Fact]
    public void ProjectReviewTab_NewReview_ShowsTheDefaultInstructionsAndTheRepositoriesToClone()
    {
        _mockStore.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PipelineJobTemplate { Id = "t1", Name = "api", IssueProviderId = "i1", RepoProviderId = "r1", Enabled = true },
            new PipelineJobTemplate { Id = "t2", Name = "web", IssueProviderId = "i2", RepoProviderId = "r2", Enabled = true }
        ]);
        var cut = RenderProject(new PipelineProject { Id = "p1", Name = "Shop", TemplateIds = ["t1", "t2"] });

        OpenTab(cut, "Project Review");

        Assert.False(cut.Find("#project-review-enabled").HasAttribute("checked"));
        Assert.Equal(PipelineConfigurationDefaults.DefaultProjectReviewPrompt, cut.Find("#project-review-prompt").GetAttribute("value"));
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("Reset to default")).HasAttribute("disabled"));
        Assert.Contains("api, web", cut.Markup);
    }

    [Fact]
    public void ProjectReviewTab_SaveWithTheDefaultInstructions_StoresThemEmpty()
    {
        // Empty instructions mean the default ones, so the project follows later changes to the default
        PipelineProject? saved = null;
        var cut = RenderProject(new PipelineProject { Id = "p1", Name = "Shop" }, p => saved = p);
        OpenTab(cut, "Project Review");

        cut.Find("#project-review-enabled").Change(true);
        ClickButton(cut, "Save Project Review");

        Assert.NotNull(saved);
        Assert.True(saved!.ProjectReviewEnabled);
        var reviewer = Assert.Single(saved.ProjectReviewers);
        Assert.Equal(PipelineConfigurationDefaults.DefaultProjectReviewerName, reviewer.Name);
        Assert.Equal("", reviewer.Prompt);
    }

    [Fact]
    public void ProjectReviewTab_SaveWithOwnInstructions_EditsTheFirstReviewerAndKeepsTheOthers()
    {
        // The page edits one reviewer; further reviewers, set through the API, stay as they are
        PipelineProject? saved = null;
        var cut = RenderProject(new PipelineProject
        {
            Id = "p1",
            Name = "Shop",
            ProjectReviewEnabled = true,
            ProjectReviewers =
            [
                new ReviewAgent { Name = "Product", Prompt = "Old instructions" },
                new ReviewAgent { Name = "Contracts", Prompt = "Check the API contracts" }
            ]
        }, p => saved = p);
        OpenTab(cut, "Project Review");
        Assert.Equal("Old instructions", cut.Find("#project-review-prompt").GetAttribute("value"));

        cut.Find("#project-review-prompt").Change("New instructions");
        ClickButton(cut, "Save Project Review");

        Assert.Equal(
            [("Product", "New instructions"), ("Contracts", "Check the API contracts")],
            saved!.ProjectReviewers.Select(r => (r.Name, r.Prompt)));
    }

    [Fact]
    public void SettingsSave_AfterAProjectReviewSave_KeepsTheProjectReview()
    {
        // The Settings tab saves its own copy of the project; it must take the project review from the saved project
        var saves = new List<PipelineProject>();
        var cut = RenderProject(new PipelineProject { Id = "p1", Name = "Shop" }, saves.Add);
        OpenTab(cut, "Project Review");
        cut.Find("#project-review-enabled").Change(true);
        ClickButton(cut, "Save Project Review");

        OpenTab(cut, "Settings");
        ClickButton(cut, "Save Settings");

        Assert.Equal(2, saves.Count);
        Assert.True(saves[1].ProjectReviewEnabled);
        Assert.Single(saves[1].ProjectReviewers);
    }
}
