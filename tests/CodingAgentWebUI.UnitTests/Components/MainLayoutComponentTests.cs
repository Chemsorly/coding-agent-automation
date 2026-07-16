using Bunit;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Configuration;
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
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineRunSummary>());

        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            historyService: mockHistory.Object);

        Services.AddSingleton<IPipelineLoopService>(new PipelineLoopService(pipelineService, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new ConsolidationBadgeService());
        Services.AddSingleton(_jsMock.Object);

        // Health indicators component dependencies
        var emptyConfig = new ConfigurationBuilder().Build();
        var emptyServiceProvider = new ServiceCollection().BuildServiceProvider();
        Services.AddSingleton(new InfrastructureHealthService(emptyServiceProvider, emptyConfig));
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(mockLogger.Object));
    }

    [Fact]
    public void Sidebar_RendersToggleButton()
    {
        var cut = Render<MainLayout>();
        var toggle = cut.Find(".sidebar-collapse-toggle");
        Assert.NotNull(toggle);
        var svg = toggle.QuerySelector("svg[data-icon='chevrons-left']");
        Assert.NotNull(svg);
    }

    [Fact]
    public void Sidebar_RendersIconComponents()
    {
        var cut = Render<MainLayout>();
        var iconSpans = cut.FindAll(".sidebar-icon svg");
        // TODO: Assertion is too weak — should assert exact expected count (7) to catch unintended additions or duplications
        Assert.True(iconSpans.Count >= 7, $"Expected at least 7 sidebar icon SVGs, found {iconSpans.Count}");
    }

    [Fact]
    public void Sidebar_IconsHaveAriaHidden()
    {
        var cut = Render<MainLayout>();
        var iconSpans = cut.FindAll(".sidebar-icon[aria-hidden='true']");
        // TODO: Assertion is too weak — should assert exact expected count (7) to detect regressions where new icons lack aria-hidden
        Assert.True(iconSpans.Count >= 7, $"Expected at least 7 icon spans with aria-hidden, found {iconSpans.Count}");
    }

    [Fact]
    public void Sidebar_ThemeToggle_ShowsCorrectIcon()
    {
        var cut = Render<MainLayout>();
        // Default theme is "dark", so sun icon should be shown
        var themeButton = cut.Find(".sidebar-theme-toggle");
        var svg = themeButton.QuerySelector("svg[data-icon='sun']");
        Assert.NotNull(svg);
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
        Assert.True(labels.Count >= 7);
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
        _jsMock.Setup(js => js.InvokeAsync<string?>("getSidebarCollapsed", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        var cut = Render<MainLayout>();
        cut.Find(".sidebar-collapse-toggle").Click();

        _jsMock.Verify(js => js.InvokeAsync<IJSVoidResult>("setSidebarCollapsed",
            It.Is<object[]>(args => args.Length > 0 && (string)args[0] == "true")), Times.Once);
    }
}
