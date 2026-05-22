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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit tests for dispatch visual feedback on the AgentCoding page (Template Table UI).
/// Covers: template table rendering, loop start/stop, drawer open/close.
/// </summary>
public class DispatchFeedbackComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;

    public DispatchFeedbackComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockJobDispatcher = new Mock<IJobDispatcher>();

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        var pipelineService = new PipelineOrchestrationService(
            _mockStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object);

        SetupDefaults();

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton(new PipelineLoopService(pipelineService, _mockFactory.Object, _mockStore.Object, _mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new Mock<IJSRuntime>().Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton(new JobDispatcherService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IJobDispatcher>(_mockJobDispatcher.Object);
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
                WorkspaceBaseDirectory = Path.GetTempPath(),
                PipelineJobTemplates = new List<PipelineJobTemplate>
                {
                    new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
                    new() { Id = "t-2", Name = "Python Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = false }
                }
            });
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "42", Title = "Test Issue", Labels = new[] { "agent:next" } },
                    new() { Identifier = "43", Title = "Bug Fix", Labels = new[] { "bug" } }
                },
                Page = 1,
                PageSize = 25,
                HasMore = false
            });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);

        _mockRepoProvider.Setup(r => r.GetAgentPullRequestsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>());
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockRepoProvider.Object);
    }

    [Fact]
    public void TemplateTable_ShowsMultipleTemplates()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("DotNet Repo", component.Markup);
        Assert.Contains("Python Repo", component.Markup);
    }

    [Fact]
    public void TemplateTable_ShowsEnabledAndDisabledTemplates()
    {
        var component = Render<AgentCoding>();

        // Both templates should be visible in the table
        Assert.Contains("DotNet Repo", component.Markup);
        Assert.Contains("Python Repo", component.Markup);
        // Toggle switches should be present
        var toggles = component.FindAll("input[type='checkbox']");
        Assert.True(toggles.Count >= 2);
    }

    [Fact]
    public void ManualDispatch_DropdownShowsOnlyEnabledTemplates()
    {
        var component = Render<AgentCoding>();

        // The manual dispatch dropdown should only show enabled templates
        var selects = component.FindAll("select");
        var dispatchSelect = selects.Last(); // The manual dispatch dropdown is the last select
        Assert.Contains("DotNet Repo", dispatchSelect.InnerHtml);
        // Python Repo is disabled, should not appear in manual dispatch dropdown
        Assert.DoesNotContain("Python Repo", dispatchSelect.InnerHtml);
    }

    [Fact]
    public void BrowseIssues_DisabledWhenNoTemplateSelected()
    {
        var component = Render<AgentCoding>();

        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Issues"));
        Assert.True(browseBtn.HasAttribute("disabled"));
    }

    [Fact]
    public async Task BrowseIssues_OpensDrawer_WhenTemplateSelected()
    {
        var component = Render<AgentCoding>();

        // Select a template in the manual dispatch dropdown
        var selects = component.FindAll("select");
        var dispatchSelect = selects.Last();
        await component.InvokeAsync(() => dispatchSelect.Change("t-1"));

        // Click Browse Issues
        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Issues"));
        await component.InvokeAsync(() => browseBtn.Click());

        // Drawer should open
        component.WaitForAssertion(() => Assert.Contains("dispatch-drawer open", component.Markup),
            timeout: TimeSpan.FromSeconds(5));
        Assert.Contains("DotNet Repo", component.Markup);
    }

    [Fact]
    public async Task Drawer_ShowsIssueList()
    {
        var component = Render<AgentCoding>();

        var selects = component.FindAll("select");
        var dispatchSelect = selects.Last();
        await component.InvokeAsync(() => dispatchSelect.Change("t-1"));

        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Issues"));
        await component.InvokeAsync(() => browseBtn.Click());

        // Issues should be loaded and displayed
        component.WaitForAssertion(() => Assert.Contains("#42", component.Markup),
            timeout: TimeSpan.FromSeconds(5));
        Assert.Contains("Test Issue", component.Markup);
        Assert.Contains("#43", component.Markup);
    }

    [Fact]
    public async Task Drawer_CloseButton_ClosesDrawer()
    {
        var component = Render<AgentCoding>();

        var selects = component.FindAll("select");
        var dispatchSelect = selects.Last();
        await component.InvokeAsync(() => dispatchSelect.Change("t-1"));

        var browseBtn = component.FindAll("button").First(b => b.TextContent.Contains("Browse Issues"));
        await component.InvokeAsync(() => browseBtn.Click());

        component.WaitForAssertion(() => Assert.Contains("dispatch-drawer open", component.Markup),
            timeout: TimeSpan.FromSeconds(5));

        // Click close button
        var closeBtn = component.Find(".agent-detail-close");
        await component.InvokeAsync(() => closeBtn.Click());

        // Drawer should close
        Assert.DoesNotContain("dispatch-drawer open", component.Markup);
    }
}
