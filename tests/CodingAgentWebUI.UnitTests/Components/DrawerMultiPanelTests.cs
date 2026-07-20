using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// Tests for dispatch drawer multi-panel management: mutual exclusion and tab switching.
/// </summary>
public class DrawerMultiPanelTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;

    public DrawerMultiPanelTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockWorkDistributor.Setup(w => w.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)>());

        var mockLogger = new Mock<Serilog.ILogger>();

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineRunSummary>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupDefaults();

        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<IPipelineLoopService>(new PipelineLoopService(runCreator, _mockFactory.Object, _mockStore.Object, _mockStore.Object, _mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new Mock<IJSRuntime>().Object);

        Services.AddSingleton<IProjectStore>(_mockStore.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDeduplicationGuardService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));

        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath()
            });
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true, ReviewEnabled = true, DecompositionEnabled = true }
            });

        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "42", Title = "Test Issue", Labels = new[] { "agent:next" } }
                },
                Page = 1,
                PageSize = 15,
                HasMore = false
            });

        _mockIssueProvider.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>
                {
                    new() { Number = 99, Identifier = "99", Title = "Fix Bug PR", Description = "Fixes a bug", Labels = new[] { "agent:next" }, BranchName = "fix/bug", TargetBranch = "main", Url = "https://github.com/test/repo/pull/99", IsDraft = false }
                },
                Page = 1,
                PageSize = 15,
                HasMore = false
            });

        _mockRepoProvider.Setup(r => r.GetAgentPullRequestsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>());

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockRepoProvider.Object);
    }

    private async Task<IRenderedComponent<AgentCoding>> RenderAndSelectTemplate()
    {
        var component = Render<AgentCoding>();
        var selects = component.FindAll("select");
        var dispatchSelect = selects.Last();
        await component.InvokeAsync(() => dispatchSelect.Change("t-1"));
        return component;
    }

    private async Task OpenIssueDrawer(IRenderedComponent<AgentCoding> component)
    {
        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Issues"));
        await component.InvokeAsync(() => browseBtn.Click());
        component.WaitForAssertion(() => Assert.Contains("dispatch-drawer open", component.Markup),
            timeout: TimeSpan.FromSeconds(5));
    }

    private async Task OpenPrDrawer(IRenderedComponent<AgentCoding> component)
    {
        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Pull Requests"));
        await component.InvokeAsync(() => browseBtn.Click());
        component.WaitForAssertion(() => Assert.Contains("dispatch-drawer open", component.Markup),
            timeout: TimeSpan.FromSeconds(5));
    }

    private async Task OpenEpicDrawer(IRenderedComponent<AgentCoding> component)
    {
        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Epics"));
        await component.InvokeAsync(() => browseBtn.Click());
        component.WaitForAssertion(() => Assert.Contains("dispatch-drawer open", component.Markup),
            timeout: TimeSpan.FromSeconds(5));
    }

    // ── Mutual Exclusion Tests ──

    [Fact]
    public async Task OpeningIssueDrawer_ClosesOtherDrawers()
    {
        var component = await RenderAndSelectTemplate();

        // Open PR drawer first
        await OpenPrDrawer(component);

        // Now open Issue drawer
        await OpenIssueDrawer(component);

        // Only one drawer should be open
        var openDrawers = component.FindAll(".dispatch-drawer.open");
        Assert.Single(openDrawers);
        // Should show issue content
        Assert.Contains("#42", component.Markup);
    }

    [Fact]
    public async Task OpeningPrDrawer_ClosesOtherDrawers()
    {
        var component = await RenderAndSelectTemplate();

        // Open Issue drawer first
        await OpenIssueDrawer(component);

        // Now open PR drawer
        await OpenPrDrawer(component);

        // Only one drawer should be open
        var openDrawers = component.FindAll(".dispatch-drawer.open");
        Assert.Single(openDrawers);
    }

    [Fact]
    public async Task OpeningEpicDrawer_ClosesOtherDrawers()
    {
        var component = await RenderAndSelectTemplate();

        // Open Issue drawer first
        await OpenIssueDrawer(component);

        // Now open Epic drawer
        await OpenEpicDrawer(component);

        // Only one drawer should be open
        var openDrawers = component.FindAll(".dispatch-drawer.open");
        Assert.Single(openDrawers);
    }

    [Fact]
    public async Task OnlyOneDrawerOpen_AtAnyTime()
    {
        var component = await RenderAndSelectTemplate();

        // Open Issue drawer
        await OpenIssueDrawer(component);
        Assert.Single(component.FindAll(".dispatch-drawer.open"));

        // Switch to PR drawer
        await OpenPrDrawer(component);
        Assert.Single(component.FindAll(".dispatch-drawer.open"));

        // Switch to Epic drawer
        await OpenEpicDrawer(component);
        Assert.Single(component.FindAll(".dispatch-drawer.open"));
    }

    // ── Tab Bar Tests ──

    [Fact]
    public async Task TabBar_RendersWhenDrawerOpen()
    {
        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        var tabBar = component.Find(".dispatch-drawer-tabs");
        Assert.NotNull(tabBar);
        Assert.Equal("tablist", tabBar.GetAttribute("role"));
    }

    [Fact]
    public async Task TabBar_ShowsActiveTabForIssueDrawer()
    {
        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        var tabs = component.FindAll(".dispatch-drawer-tab");
        var activeTab = tabs.First(t => t.ClassList.Contains("active"));
        Assert.Contains("Issues", activeTab.TextContent);
        Assert.Equal("true", activeTab.GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task TabBar_ClickingPrTab_SwitchesToPrDrawer()
    {
        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        // Click PR tab
        var tabs = component.FindAll(".dispatch-drawer-tab");
        var prTab = tabs.First(t => t.TextContent.Contains("Pull Requests"));
        await component.InvokeAsync(() => prTab.Click());

        // Should switch to PR drawer
        component.WaitForAssertion(() =>
        {
            var openDrawers = component.FindAll(".dispatch-drawer.open");
            Assert.Single(openDrawers);
            Assert.Contains("#99", component.Markup);
        }, timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TabBar_ClickingEpicTab_SwitchesToEpicDrawer()
    {
        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        // Click Epic tab
        var tabs = component.FindAll(".dispatch-drawer-tab");
        var epicTab = tabs.First(t => t.TextContent.Contains("Epics"));
        await component.InvokeAsync(() => epicTab.Click());

        // Should switch to Epic drawer — only one drawer open
        component.WaitForAssertion(() =>
        {
            var openDrawers = component.FindAll(".dispatch-drawer.open");
            Assert.Single(openDrawers);
        }, timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TabBar_ActiveTabHasAriaSelected()
    {
        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        // Scope to the open drawer only
        var openDrawer = component.Find(".dispatch-drawer.open");
        var tabs = openDrawer.QuerySelectorAll("[role='tab']");
        // Active tab (Issues) should have aria-selected="true"
        var activeTab = tabs.First(t => t.TextContent.Contains("Issues"));
        Assert.Equal("true", activeTab.GetAttribute("aria-selected"));

        // Inactive tabs should have aria-selected="false"
        var inactiveTabs = tabs.Where(t => !t.TextContent.Contains("Issues"));
        foreach (var tab in inactiveTabs)
        {
            Assert.Equal("false", tab.GetAttribute("aria-selected"));
        }
    }

    [Fact]
    public async Task TabBar_RespectsTemplateCapabilities_HidesPrTab_WhenReviewDisabled()
    {
        // Override template to disable review
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true, ReviewEnabled = false, DecompositionEnabled = true }
            });

        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        // Scope to the open drawer
        var openDrawer = component.Find(".dispatch-drawer.open");
        var tabs = openDrawer.QuerySelectorAll(".dispatch-drawer-tab");
        Assert.DoesNotContain(tabs, t => t.TextContent.Contains("Pull Requests"));
        Assert.Contains(tabs, t => t.TextContent.Contains("Epics"));
    }

    [Fact]
    public async Task TabBar_RespectsTemplateCapabilities_HidesEpicTab_WhenDecompositionDisabled()
    {
        // Override template to disable decomposition
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true, ReviewEnabled = true, DecompositionEnabled = false }
            });

        var component = await RenderAndSelectTemplate();
        await OpenIssueDrawer(component);

        // Scope to the open drawer
        var openDrawer = component.Find(".dispatch-drawer.open");
        var tabs = openDrawer.QuerySelectorAll(".dispatch-drawer-tab");
        Assert.Contains(tabs, t => t.TextContent.Contains("Pull Requests"));
        Assert.DoesNotContain(tabs, t => t.TextContent.Contains("Epics"));
    }

    // ── Cache-Aware Switching Tests ──

    [Fact]
    public async Task TabSwitch_BackToIssueDrawer_ReusesCache()
    {
        var component = await RenderAndSelectTemplate();

        // Open issues first
        await OpenIssueDrawer(component);
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup),
            timeout: TimeSpan.FromSeconds(5));

        // Switch to PR via tab (scope to open drawer)
        var openDrawer = component.Find(".dispatch-drawer.open");
        var prTab = openDrawer.QuerySelectorAll(".dispatch-drawer-tab").First(t => t.TextContent.Contains("Pull Requests"));
        await component.InvokeAsync(() => prTab.Click());

        component.WaitForAssertion(() => Assert.Contains("#99", component.Markup),
            timeout: TimeSpan.FromSeconds(5));

        // Switch back to Issues via tab (scope to open drawer)
        var prDrawer = component.Find(".dispatch-drawer.open");
        var issueTab = prDrawer.QuerySelectorAll(".dispatch-drawer-tab").First(t => t.TextContent.Contains("Issues"));
        await component.InvokeAsync(() => issueTab.Click());

        // Issue data should reappear without an additional load call
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup),
            timeout: TimeSpan.FromSeconds(5));

        // Verify issue provider was called only once (initial open)
        _mockIssueProvider.Verify(
            p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
