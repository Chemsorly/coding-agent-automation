using Bunit;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

public class MainLayoutComponentTests : BunitContext
{
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

        var pipelineService = new PipelineOrchestrationService(
            mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), new CiLogWriter(mockLogger.Object), new FeedbackService(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistory.Object);

        Services.AddSingleton(new PipelineLoopService(pipelineService, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new ConsolidationBadgeService());
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    // TODO: Add test that configures IJSRuntime mock to return "true" from getSidebarCollapsed
    // and verifies sidebar renders with "collapsed" class on init (persistence across refreshes)
    // TODO: Add test that verifies setSidebarCollapsed is called on the IJSRuntime mock when toggling
    [Fact]
    public void Sidebar_RendersToggleButton()
    {
        var cut = Render<MainLayout>();
        var toggle = cut.Find(".sidebar-toggle");
        Assert.NotNull(toggle);
        Assert.Equal("«", toggle.TextContent);
    }

    [Fact]
    public void Sidebar_CollapseToggle_AddsCssClass()
    {
        var cut = Render<MainLayout>();
        var nav = cut.Find("nav.sidebar");
        Assert.DoesNotContain("collapsed", nav.ClassList);

        cut.Find(".sidebar-toggle").Click();

        nav = cut.Find("nav.sidebar");
        Assert.Contains("collapsed", nav.ClassList);
    }

    [Fact]
    public void Sidebar_CollapseToggle_ChangesAriaExpanded()
    {
        var cut = Render<MainLayout>();
        var toggle = cut.Find(".sidebar-toggle");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));

        toggle.Click();

        toggle = cut.Find(".sidebar-toggle");
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
        Assert.True(labels.Count >= 7); // brand + 6 nav links
    }
}
