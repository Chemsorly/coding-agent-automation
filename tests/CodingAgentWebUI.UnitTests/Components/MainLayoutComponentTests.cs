using Bunit;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

public class MainLayoutComponentTests : BunitContext
{
    private readonly Mock<IJSRuntime> _jsMock = new();

    public MainLayoutComponentTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockStore = new Mock<IConfigurationStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            historyService: mockHistory.Object);

        Services.AddSingleton(new PipelineLoopService(pipelineService, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new ConsolidationBadgeService());
        Services.AddSingleton(new ProjectChangeNotifier());
        Services.AddSingleton<IProjectStore>(mockStore.Object);
        Services.AddSingleton(_jsMock.Object);
    }

    [Fact]
    public void Sidebar_RendersToggleButton()
    {
        var cut = Render<MainLayout>();
        var toggle = cut.Find(".sidebar-collapse-toggle");
        Assert.NotNull(toggle);
        Assert.Equal("«", toggle.TextContent);
    }

    [Fact]
    public void Sidebar_CollapseToggle_AddsCssClass()
    {
        var cut = Render<MainLayout>();
        var nav = cut.Find("nav.sidebar");
        Assert.DoesNotContain("collapsed", nav.ClassList);

        cut.Find(".sidebar-collapse-toggle").Click();

        nav = cut.Find("nav.sidebar");
        Assert.Contains("collapsed", nav.ClassList);
    }

    [Fact]
    public void Sidebar_CollapseToggle_ChangesAriaExpanded()
    {
        var cut = Render<MainLayout>();
        var toggle = cut.Find(".sidebar-collapse-toggle");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));

        toggle.Click();

        toggle = cut.Find(".sidebar-collapse-toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Sidebar_HamburgerToggle_TogglesOverlayOpen()
    {
        var cut = Render<MainLayout>();
        var nav = cut.Find("nav.sidebar");
        Assert.DoesNotContain("overlay-open", nav.ClassList);

        cut.Find(".hamburger-toggle").Click();

        nav = cut.Find("nav.sidebar");
        Assert.Contains("overlay-open", nav.ClassList);
    }

    [Fact]
    public void Sidebar_OverlayBackdropClick_ClosesOverlay()
    {
        var cut = Render<MainLayout>();
        cut.Find(".hamburger-toggle").Click();

        var nav = cut.Find("nav.sidebar");
        Assert.Contains("overlay-open", nav.ClassList);

        cut.Find(".sidebar-overlay-backdrop").Click();

        nav = cut.Find("nav.sidebar");
        Assert.DoesNotContain("overlay-open", nav.ClassList);
    }

    [Fact]
    public void Sidebar_RendersLabelsWithCorrectClass()
    {
        var cut = Render<MainLayout>();
        var labels = cut.FindAll(".sidebar-label");
        Assert.True(labels.Count >= 7); // TODO: Assertion is too weak — consider Assert.InRange(labels.Count, 7, 8) to catch unexpected extra labels while still allowing the optional project indicator
    }

    [Fact]
    public void Sidebar_LoadsCollapsedStateFromLocalStorage()
    {
        _jsMock.Setup(js => js.InvokeAsync<string?>("getSidebarCollapsed", It.IsAny<object[]>()))
            .ReturnsAsync("true");

        var cut = Render<MainLayout>();

        var nav = cut.Find("nav.sidebar");
        Assert.Contains("collapsed", nav.ClassList);
    }

    [Fact]
    public void Sidebar_ToggleCollapse_PersistsToLocalStorage()
    {
        // TODO: Only tests persisting "true" (collapsed). Add a test that verifies persisting "false" when expanding back.
        _jsMock.Setup(js => js.InvokeAsync<string?>("getSidebarCollapsed", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        var cut = Render<MainLayout>();
        cut.Find(".sidebar-collapse-toggle").Click();

        _jsMock.Verify(js => js.InvokeAsync<IJSVoidResult>("setSidebarCollapsed",
            It.Is<object[]>(args => args.Length > 0 && (string)args[0] == "true")), Times.Once);
    }

    [Fact]
    // TODO: Add test that verifies project indicator updates when ProjectChangeNotifier.OnChange fires
    // (acceptance criterion: "Project name updates if the active project changes").
    // TODO: These project indicator tests register a second IProjectStore after the constructor already
    // registered one. This relies on Microsoft DI resolving the last registration, which is fragile.
    // Consider making the constructor's mock reconfigurable or removing the base registration.
    public void Sidebar_ShowsProjectIndicator_WhenUserProjectExists()
    {
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = "proj-1", Name = "My Project", Enabled = true, TemplateIds = [] }
            });
        Services.AddSingleton<IProjectStore>(mockProjectStore.Object);

        var cut = Render<MainLayout>();

        var indicator = cut.Find(".sidebar-project-indicator");
        Assert.NotNull(indicator);
        Assert.Contains("My Project", indicator.TextContent);
    }

    [Fact]
    public void Sidebar_HidesProjectIndicator_WhenOnlyDefaultProjectExists()
    {
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = [] }
            });
        Services.AddSingleton<IProjectStore>(mockProjectStore.Object);

        var cut = Render<MainLayout>();

        Assert.Empty(cut.FindAll(".sidebar-project-indicator"));
    }

    [Fact]
    public void Sidebar_HidesProjectIndicator_WhenNoProjectsExist()
    {
        // TODO: This test relies implicitly on the constructor's mock returning an empty list.
        // Consider setting up an explicit mock to make the test self-contained.
        // Default constructor mock already returns empty list — just verify
        var cut = Render<MainLayout>();

        Assert.Empty(cut.FindAll(".sidebar-project-indicator"));
    }

    [Fact]
    public void Sidebar_ShowsMultipleProjects_WhenMultipleExist()
    {
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = "proj-1", Name = "Alpha", Enabled = true, TemplateIds = [] },
                new() { Id = "proj-2", Name = "Beta", Enabled = true, TemplateIds = [] }
            });
        Services.AddSingleton<IProjectStore>(mockProjectStore.Object);

        var cut = Render<MainLayout>();

        var indicator = cut.Find(".sidebar-project-indicator");
        Assert.Contains("Alpha", indicator.TextContent);
        Assert.Contains("Beta", indicator.TextContent);
    }

    [Fact]
    public void Sidebar_ProjectIndicator_FiltersDisabledProjects()
    {
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = "proj-1", Name = "Active", Enabled = true, TemplateIds = [] },
                new() { Id = "proj-2", Name = "Disabled", Enabled = false, TemplateIds = [] }
            });
        Services.AddSingleton<IProjectStore>(mockProjectStore.Object);

        var cut = Render<MainLayout>();

        var indicator = cut.Find(".sidebar-project-indicator");
        Assert.Contains("Active", indicator.TextContent);
        Assert.DoesNotContain("Disabled", indicator.TextContent);
    }
}
