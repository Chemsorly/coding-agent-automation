using Bunit;
using Moq;
using CodingAgentWebUI.Api.Client;
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
/// bUnit tests for dispatch visual feedback on the AgentCoding page (Template Table UI).
/// Covers: template table rendering, loop start/stop, drawer open/close.
/// </summary>
public class DispatchFeedbackComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;

    public DispatchFeedbackComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockWorkDistributor.Setup(w => w.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)>());

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();

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
        // Spec 047: component injects ILoopStatusService (not IPipelineLoopService)
        var mockLoopStatus = new Mock<ILoopStatusService>();
        mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(false);
        mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("");
        mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());
        mockLoopStatus.SetupGet(l => l.TemplateStatuses)
            .Returns(new Dictionary<string, CodingAgentWebUI.Pipeline.Models.ConfigStatusSnapshot>());
        mockLoopStatus.SetupGet(l => l.IsSchedulerUnreachable).Returns(false);
        Services.AddSingleton<ILoopStatusService>(mockLoopStatus.Object);
        var mockSchedulerClient = new Mock<ISchedulerApiClient>();
        mockSchedulerClient.Setup(c => c.StartLoopAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new LoopStartResultDto(true, null));
        mockSchedulerClient.Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Services.AddSingleton<ISchedulerApiClient>(mockSchedulerClient.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);

        Services.AddSingleton<IProjectStore>(_mockStore.Object);

        // Spec 045: AgentCodingPageService now uses IPipelineApiConfigClient.
        var mockConfigClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        // AgentCodingPageService feeds the dispatch path, so it reads the with-secrets form
        // (live tokens/base URLs) rather than the "****" masked one. Same backing store.
        mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadPipelineConfigAsync(ct));
        mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAllTemplatesAsync(ct));
        mockConfigClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadProjectsAsync(ct));
        mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadQualityGateConfigsAsync(ct));
        mockConfigClient.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadReviewerConfigsAsync(ct));
        mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAgentProfilesAsync(ct));
        mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockConfigClient.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Services.AddSingleton<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>(mockConfigClient.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDeduplicationGuardService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton<IDispatchOrchestrationService>(new Mock<IDispatchOrchestrationService>().Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
        Services.AddEmbeddedConsolidationDeps();
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
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = new[] { "t-1", "t-2" } }
            });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
                new() { Id = "t-2", Name = "Python Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = false }
            });

        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "42", Title = "Test Issue", Labels = new[] { "agent:next" } },
                    new() { Identifier = "43", Title = "Bug Fix", Labels = new[] { "bug" } }
                },
                Page = 1,
                PageSize = 15,
                HasMore = false
            });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);

        _mockRepoProvider.Setup(r => r.GetAgentPullRequestsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
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
        var dispatchSelect = selects[^1]; // The manual dispatch dropdown is the last select
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
        var dispatchSelect = selects[^1];
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
        var dispatchSelect = selects[^1];
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
        var dispatchSelect = selects[^1];
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
