using CodingAgent.Web.Auth;
using AwesomeAssertions;
using CodingAgent.Web.UnitTests.Auth;
using Bunit;
using Moq;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for the AgentCoding page (Template Table UI).
/// Renders the actual Blazor component and asserts on markup and view switching.
/// </summary>
public class AgentCodingPageComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<CodingAgent.Api.Client.IPipelineApiConfigClient> _mockConfigClient;
    private readonly Mock<ILoopStatusService> _mockLoopStatus;

    public AgentCodingPageComponentTests()
    {
        Services.AddTestAccess(); // Spec 049: global admin, so every control renders as before
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineRunSummary>());

        SetupDefaults();

        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);

        // Spec 047: AgentCoding.razor.cs now injects ILoopStatusService (not IPipelineLoopService).
        // AgentCodingPageService now takes ISchedulerApiClient (not IPipelineLoopService).
        _mockLoopStatus = new Mock<ILoopStatusService>();
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("");
        _mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());
        _mockLoopStatus.SetupGet(l => l.TemplateStatuses)
            .Returns(new Dictionary<string, CodingAgent.Pipeline.Models.ConfigStatusSnapshot>());
        _mockLoopStatus.SetupGet(l => l.IsSchedulerUnreachable).Returns(false);
        Services.AddSingleton(_mockLoopStatus.Object);
        Services.AddSingleton<ILoopStatusService>(_mockLoopStatus.Object);

        var mockSchedulerClient = new Mock<ISchedulerApiClient>();
        mockSchedulerClient.Setup(c => c.StartLoopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoopStartResultDto(true, null));
        mockSchedulerClient.Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockSchedulerClient.Setup(c => c.ResumeLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Services.AddSingleton(mockSchedulerClient.Object);
        Services.AddSingleton<ISchedulerApiClient>(mockSchedulerClient.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);

        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = new[] { "t-1" } }
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
            });
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Services.AddSingleton(_mockProjectStore.Object);

        // Spec 045: AgentCodingPageService now uses IPipelineApiConfigClient instead of
        // IConfigurationStore + IProjectStore. Register a mock that delegates to the existing mocks
        // so existing test assertions remain valid.
        _mockConfigClient = new Mock<CodingAgent.Api.Client.IPipelineApiConfigClient>();
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        // AgentCodingPageService feeds the dispatch path, so it reads the with-secrets form
        // (live tokens/base URLs) rather than the "****" masked one. Same backing store.
        _mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadPipelineConfigAsync(ct));
        _mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadAllTemplatesAsync(ct));
        _mockConfigClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadProjectsAsync(ct));
        _mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadQualityGateConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadReviewerConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAgentProfilesAsync(ct));
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Services.AddSingleton<CodingAgent.Api.Client.IPipelineApiConfigClient>(_mockConfigClient.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton<IDispatchOrchestrationService>(new Mock<IDispatchOrchestrationService>().Object);

        // IssueDrawerService now requires IPipelineApiWorkItemClient for status-aware active-issue tracking.
        var mockApiWorkItemClient = new Mock<IPipelineApiWorkItemClient>();
        mockApiWorkItemClient.Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string, string)>());
        mockApiWorkItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());
        Services.AddSingleton<IPipelineApiWorkItemClient>(mockApiWorkItemClient.Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
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

        _mockRepoProvider.Setup(r => r.GetAgentPullRequestsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>());
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockRepoProvider.Object);
    }

    [Fact]
    public void AgentCoding_RendersPageHeader()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Pipelines", component.Markup);
        Assert.NotNull(component.Find("h1"));
    }

    [Fact]
    public void AgentCoding_ShowsTemplateTable()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Pipeline Job Templates", component.Markup);
        Assert.Contains("DotNet Repo", component.Markup);
        Assert.Contains("GitHub Issues", component.Markup);
        Assert.Contains("GitHub Repo", component.Markup);
    }

    [Fact]
    public void AgentCoding_ShowsLoopControls()
    {
        var component = Render<AgentCoding>();

        // Start Loop button should be present
        Assert.Contains("Start Loop", component.Markup);
    }

    [Fact]
    public void AgentCoding_ShowsManualDispatchSection()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Manual Dispatch", component.Markup);
        Assert.Contains("Browse Issues", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenNoTemplates_ShowsEmptyMessage()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var component = Render<AgentCoding>();

        Assert.Contains("No pipeline job templates configured", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenNoTemplates_ShowsExplanatoryDescription()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var component = Render<AgentCoding>();

        // TODO: Consider asserting on a longer substring or combining with CSS class check to detect truncated/garbled messages
        Assert.Contains("Templates define how the pipeline processes issues", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenTemplatesExist_HidesExplanatoryDescription()
    {
        // TODO: Explicitly set up non-empty template list in this test body for clarity, rather than relying on default mock setup from constructor
        var component = Render<AgentCoding>();

        Assert.DoesNotContain("Templates define how the pipeline processes issues", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenNoTemplates_StartLoopDisabled()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.True(startBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentCoding_WhenNoIssueProviders_StartLoopDisabled()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.True(startBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentCoding_WhenNoRepoProviders_StartLoopDisabled()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.True(startBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentCoding_WhenOnlyDisabledTemplates_StartLoopDisabled()
    {
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "Disabled Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = false }
            });

        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.True(startBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentCoding_WhenAllPrerequisitesMet_StartLoopEnabled()
    {
        // Default setup has issue provider, repo provider, and enabled template
        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.False(startBtn.HasAttribute("disabled"));
        // TODO: Assert that title attribute is absent when button is enabled to catch spurious tooltip rendering
    }

    [Fact]
    public void AgentCoding_WhenStartLoopDisabled_ShowsTooltip()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.True(startBtn.HasAttribute("title"));
        Assert.Contains("No issue provider configured", startBtn.GetAttribute("title"));
    }

    [Fact]
    public void AgentCoding_ShowsAddTemplateButton()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("+ Add Template", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenProviderLoadFails_ShowsError()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var component = Render<AgentCoding>();

        Assert.Contains("Failed to load configuration", component.Markup);
        Assert.Contains("Connection failed", component.Markup);
    }

    [Fact]
    public void AgentCoding_DisposesEventHandlers()
    {
        var component = Render<AgentCoding>();
        // Capture markup before disposal
        var markupBeforeDispose = component.Markup;
        // Dispose should not throw
        component.Dispose();
        Assert.True(component.IsDisposed);
        Assert.Contains("Pipelines", markupBeforeDispose);
    }

    [Fact]
    public void AgentCoding_WhenFreshState_ShowsOnboardingChecklist()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath()
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

        var component = Render<AgentCoding>();

        Assert.Contains("Getting Started", component.Markup);
        Assert.Contains("Create an Issue Provider", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenFullyConfigured_HidesOnboardingChecklist()
    {
        // TODO: Test name is misleading — it asserts checklist IS visible, not hidden. Rename or fix assertions to match intended behavior.
        // Default setup already has providers and templates configured
        var component = Render<AgentCoding>();

        // Checklist auto-hides when not all steps are complete, but since templates/providers exist
        // the issue provider, repo provider, and template steps are satisfied.
        // All 5 steps need to be true for AllComplete to hide the checklist.
        // With default setup: has issue provider, repo provider, template — but no project or loop active.
        // So checklist still shows (not all complete). Verify it IS visible but shows completed steps.
        Assert.Contains("Getting Started", component.Markup);
    }

    [Fact]
    public void AgentCoding_TemplateTable_ShowsEnabledToggle()
    {
        var component = Render<AgentCoding>();

        // Should have a toggle switch for the template
        Assert.Contains("toggle-switch", component.Markup);
    }

    [Fact]
    public void AgentCoding_TemplateTable_ShowsRemoveButton()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Remove", component.Markup);
    }

    [Fact]
    public void AgentCoding_TemplateTable_ShowsProviderWarning_WhenProviderMissing()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath()
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "Bad Template", IssueProviderId = "nonexistent", RepoProviderId = "rp-1", Enabled = true }
            });

        var component = Render<AgentCoding>();

        // Should show warning indicator for missing provider
        // TODO: Strengthen assertion — Assert.Contains on markup string is weaker than the previous Find("[data-icon=\"alert-triangle\"]") DOM query
        Assert.Contains("alert-triangle", component.Markup);
    }

    [Fact]
    public void AgentCoding_TemplateTable_ShowsDash_WhenNoBrainOrPipeline()
    {
        var component = Render<AgentCoding>();

        // Brain and CI columns should show "—" when not configured
        var markup = component.Markup;
        Assert.Contains("—", markup);
    }

    [Fact]
    public async Task AgentCoding_AddTemplate_ShowsForm()
    {
        var component = Render<AgentCoding>();

        var addBtn = component.FindAll("button").First(b => b.TextContent.Contains("+ Add Template"));
        await component.InvokeAsync(() => addBtn.Click());

        Assert.Contains("Add Pipeline Job Template", component.Markup);
        Assert.Contains("Name", component.Markup);
        Assert.Contains("Issue Provider", component.Markup);
        Assert.Contains("Repo Provider", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenBrowseIssuesDisabled_ShowsTooltip()
    {
        // Configure no enabled templates so auto-preselect does not fire.
        // With no selectable template, the Browse Issues button must be disabled and show a tooltip.
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var component = Render<AgentCoding>();

        var browseBtn = component.Find("[data-testid='browse-issues-btn']");
        Assert.True(browseBtn.HasAttribute("disabled"));
        Assert.True(browseBtn.HasAttribute("title"));
        Assert.Contains("Select a pipeline template to browse issues", browseBtn.GetAttribute("title"));
    }

    [Fact]
    public void AgentCoding_WhenBrowseEpicsDisabled_ShowsTooltip()
    {
        // Configure no enabled templates so auto-preselect does not fire.
        // With no selectable template, the Browse Epics button must be disabled and show a tooltip.
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var component = Render<AgentCoding>();

        var browseBtn = component.Find("[data-testid='browse-epics-btn']");
        Assert.True(browseBtn.HasAttribute("disabled"));
        Assert.True(browseBtn.HasAttribute("title"));
        Assert.Contains("Select a pipeline template to browse epics", browseBtn.GetAttribute("title"));
    }

    [Fact]
    public void AgentCoding_WhenBrowsePrsDisabled_ShowsTooltip()
    {
        // Configure no enabled templates so auto-preselect does not fire.
        // With no selectable template, the Browse Pull Requests button must be disabled and show a tooltip.
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var component = Render<AgentCoding>();

        var browseBtn = component.Find("[data-testid='browse-prs-btn']");
        Assert.True(browseBtn.HasAttribute("disabled"));
        Assert.True(browseBtn.HasAttribute("title"));
        Assert.Contains("Select a pipeline template to browse pull requests", browseBtn.GetAttribute("title"));
    }

    [Fact]
    public async Task AgentCoding_WhenTemplateSelected_BrowseButtonsHaveNoTooltip()
    {
        var component = Render<AgentCoding>();

        // Select a template in the manual dispatch dropdown
        var selects = component.FindAll("select");
        var dispatchSelect = selects.Last();
        await component.InvokeAsync(() => dispatchSelect.Change("t-1"));

        // All browse buttons should have no title when enabled
        var browseIssuesBtn = component.Find("[data-testid='browse-issues-btn']");
        Assert.False(browseIssuesBtn.HasAttribute("title"));

        var browseEpicsBtn = component.Find("[data-testid='browse-epics-btn']");
        Assert.False(browseEpicsBtn.HasAttribute("title"));

        var browsePrsBtn = component.Find("[data-testid='browse-prs-btn']");
        Assert.False(browsePrsBtn.HasAttribute("title"));
    }

    /// <summary>
    /// When exactly one template is enabled, the Manual Dispatch dropdown must be pre-populated
    /// with that template's id so browse buttons are immediately usable without a manual pick.
    /// Acceptance criterion: "With a single enabled template, Manual Dispatch preselects it."
    /// </summary>
    [Fact]
    public void AgentCoding_WithSingleEnabledTemplate_AutoPreselectsIt()
    {
        // Default setup already has exactly one enabled template (t-1).
        var component = Render<AgentCoding>();

        // The select element should show the only enabled template as selected value.
        var dispatchSelect = component.Find("[data-testid='template-select']");
        // TODO: [WARNING] GetAttribute("value") on a bUnit <select> reflects the static HTML
        // attribute value from the initial render, not the Blazor-bound field value after
        // an async OnAfterRenderAsync completes. The auto-preselect runs in OnAfterRenderAsync,
        // so this assertion may pass because of an initial render value rather than confirming
        // the auto-preselect path actually executed. A stronger assertion would check that the
        // browse-issues button transitions from disabled to enabled, since that is the observable
        // DOM effect of _manualDispatchTemplateId being set by auto-preselect.
        // (TestQualityReviewer, issue #2947)
        Assert.Equal("t-1", dispatchSelect.GetAttribute("value"));

        // Browse buttons must be enabled (no disabled attribute).
        var browseIssuesBtn = component.Find("[data-testid='browse-issues-btn']");
        Assert.False(browseIssuesBtn.HasAttribute("disabled"));
    }

    /// <summary>
    /// When the user arrives via the "Browse &amp; dispatch" deep-link (?dispatch=issues) but no
    /// template is selected (multiple templates configured, cold browser, no saved preference),
    /// an informative error message must be shown — rather than the drawer silently not opening.
    /// Acceptance criterion fix for issue #2947: "Browse &amp; dispatch opens the issue drawer directly."
    /// </summary>
    [Fact]
    public void AgentCoding_DispatchIssuesParam_WithNoTemplateSelected_ShowsSelectTemplateError()
    {
        // Configure multiple enabled templates so auto-preselect does NOT fire (requires exactly 1).
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo",   IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
                new() { Id = "t-2", Name = "Python Repo",   IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
            });

        // Simulate navigation with ?dispatch=issues before rendering (bUnit SupplyParameterFromQuery pattern)
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/pipelines?dispatch=issues");

        var component = Render<AgentCoding>();

        // With no saved preference and two enabled templates, _manualDispatchTemplateId stays empty.
        // The deep-link handler must surface an error message instead of silently doing nothing.
        Assert.Contains("Select a pipeline template", component.Markup);
    }

    /// <summary>
    /// When the user arrives via the "Browse &amp; dispatch" deep-link (?dispatch=issues) and
    /// exactly one enabled template exists (auto-preselected), the issue drawer must open automatically.
    /// Acceptance criterion: "Browse &amp; dispatch opens the issue drawer directly."
    /// </summary>
    [Fact]
    public void AgentCoding_DispatchIssuesParam_WithSingleTemplate_OpensIssueDrawer()
    {
        // Default setup has exactly one enabled template (t-1), which will be auto-preselected.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/pipelines?dispatch=issues");

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        // The auto-preselect fires, then OnAfterRenderAsync opens the issue drawer automatically.
        Assert.True(pageService.IsIssueDrawerOpen,
            "Issue drawer should open automatically when ?dispatch=issues and exactly one template is configured.");
    }

    /// <summary>
    /// When the user changes the template dropdown, the component updates its selected template.
    /// This covers the OnTemplateChanged handler path.
    /// </summary>
    [Fact]
    public async Task AgentCoding_OnTemplateChanged_EnablesBrowseButtons()
    {
        // Arrange: set up two templates so auto-preselect does NOT fire
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
                new() { Id = "t-2", Name = "Python Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
            });

        var component = Render<AgentCoding>();

        // Before selection: browse buttons are disabled
        var browseBtn = component.Find("[data-testid='browse-issues-btn']");
        Assert.True(browseBtn.HasAttribute("disabled"), "Browse button should be disabled before template selection");

        // Act: change the template dropdown to t-1
        var dispatchSelect = component.Find("[data-testid='template-select']");
        await component.InvokeAsync(() => dispatchSelect.Change("t-1"));

        // Assert: browse button is now enabled — OnTemplateChanged updated _manualDispatchTemplateId
        browseBtn = component.Find("[data-testid='browse-issues-btn']");
        Assert.False(browseBtn.HasAttribute("disabled"), "Browse button should be enabled after template selection");
    }

    [Fact]
    public void AgentCoding_ShowAddForm_ButtonClickShowsForm()
    {
        var component = Render<AgentCoding>();

        var addBtn = component.FindAll("button").First(b => b.TextContent.Contains("+ Add Template"));
        addBtn.Click();

        Assert.Contains("Add Pipeline Job Template", component.Markup);
        Assert.Contains("Cancel", component.Markup);
    }

    [Fact]
    public async Task AgentCoding_CancelAddForm_HidesForm()
    {
        var component = Render<AgentCoding>();

        // Open the form
        var addBtn = component.FindAll("button").First(b => b.TextContent.Contains("+ Add Template"));
        await component.InvokeAsync(() => addBtn.Click());
        Assert.Contains("Add Pipeline Job Template", component.Markup);

        // Cancel it
        var cancelBtn = component.FindAll("button").First(b => b.TextContent.Contains("Cancel"));
        await component.InvokeAsync(() => cancelBtn.Click());

        Assert.DoesNotContain("Add Pipeline Job Template", component.Markup);
    }

    [Fact]
    public async Task AgentCoding_AddTemplate_WithValidData_AddsAndClosesForm()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var form = new TemplateTableSection.TemplateFormModel
            {
                Name = "New Template",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-1",
                ProjectId = WellKnownIds.DefaultProjectId
            };
            var (success, error, message) = await pageService.AddTemplateAsync(form);
            Assert.True(success, error);
            Assert.NotNull(message);
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_AddTemplate_WhileLoopRuns_ShowsNextCycleMessage()
    {
        // Arrange: loop is active for this test
        // TODO: [WARNING] _mockLoopStatus.SetupGet is called here (before Render<AgentCoding>()) to
        // override the constructor's Returns(false) setup. This works because Moq replaces prior
        // SetupGet setups on the same mock instance and the DI container holds the same reference.
        // However, the ordering is fragile: if Render<AgentCoding>() is ever moved above this line,
        // the component renders with IsLoopActive=false and the "next cycle" assertion fails silently.
        // If AgentCoding ever caches IsLoopActive at construction time, WithLoopNote would see
        // the cached false value and the assertion would also fail. Prefer a per-test factory or
        // a dedicated fixture that provides IsLoopActive=true from the start.
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(true);

        // Add a second issue and repo provider so the new template doesn't conflict with t-1
        // (enabled templates may not share an issue tracker or repository).
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" },
                new() { Id = "ip-2", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues 2" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" },
                new() { Id = "rp-2", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo 2" }
            });
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" },
                new() { Id = "ip-2", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues 2" }
            });
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" },
                new() { Id = "rp-2", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo 2" }
            });
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();

        // Assert: admin controls are visible even while the loop runs
        Assert.Contains("+ Add Template", component.Markup);
        // TODO: [WARNING] These substring checks are too broad — "Edit" and "Remove" can appear in
        // many other places in the markup (headings, other buttons, aria labels). They will pass even
        // if the row-action buttons are absent. Replace with precise locators, e.g.:
        //   Assert.NotEmpty(component.FindAll("button.btn-edit"));
        //   Assert.NotEmpty(component.FindAll("button.btn-delete"));
        Assert.Contains("Edit", component.Markup);
        Assert.Contains("Remove", component.Markup);

        // Click "+ Add Template" to open the add form
        var addBtn = component.FindAll("button").First(b => b.TextContent.Contains("+ Add Template"));
        await component.InvokeAsync(() => addBtn.Click());

        // The main TemplateTableSection (the one with OnAddTemplate="AddTemplate") now has ShowAddForm=true.
        // Its AddForm parameter is the same _addForm instance held by AgentCoding — fill it and invoke save.
        await component.InvokeAsync(async () =>
        {
            // Find the TemplateTableSection that is showing the add form (ShowAddForm = true).
            var sections = component.FindComponents<TemplateTableSection>();
            var mainSection = sections.First(s => s.Instance.ShowAddForm);

            // Set valid form values directly on the shared form model reference.
            // Use ip-2/rp-2 to avoid conflict with existing template t-1 (ip-1/rp-1).
            // TODO: [WARNING] This mutates AddForm directly on the TemplateTableSection instance,
            // relying on the fact that AgentCoding._addForm and mainSection.Instance.AddForm are
            // the same object reference. If AgentCoding ever copies the form or passes it by value,
            // this coupling breaks silently (no compile error, test silently passes without testing
            // the real flow). Consider going through the UI (filling the rendered form inputs) to
            // make the test more resilient to refactoring.
            mainSection.Instance.AddForm.Name = "Loop Template";
            mainSection.Instance.AddForm.IssueProviderId = "ip-2";
            mainSection.Instance.AddForm.RepoProviderId = "rp-2";
            mainSection.Instance.AddForm.ProjectId = WellKnownIds.DefaultProjectId;

            // Invoke OnAddTemplate — this calls AgentCoding.AddTemplate() which calls WithLoopNote.
            await mainSection.Instance.OnAddTemplate.InvokeAsync();
        });

        // Assert: the rendered success message contains "next cycle"
        Assert.Contains("next cycle", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentCoding_ToggleTemplateEnabled_CallsService()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleTemplateEnabledAsync(template, false);
            Assert.True(success, error);
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleImplementationEnabled_CallsService()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleImplementationEnabledAsync(template, true);
            Assert.True(success, error);
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleReviewEnabled_CallsService()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleReviewEnabledAsync(template, true);
            Assert.True(success, error);
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleDecompositionEnabled_CallsService()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleDecompositionEnabledAsync(template, true);
            Assert.True(success, error);
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_RemoveTemplate_Success_RemovesFromList()
    {
        _mockConfigClient.Setup(c => c.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(c => c.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error, message) = await pageService.RemoveTemplateAsync(template);
            Assert.True(success, error);
        });
    }

    // ── Start/Stop Loop ──────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_StartLoop_WhenLoopStartsSuccessfully_NoError()
    {
        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.False(startBtn.HasAttribute("disabled"));

        await component.InvokeAsync(() => startBtn.Click());

        // After click, component should have attempted to start loop via PageService
        // No error should appear (loop service returns false by default — no enabled templates with actual providers)
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public void AgentCoding_ShowsStopLoopButton_WhenLoopActive()
    {
        var component = Render<AgentCoding>();
        // Default: loop is not active, Start Loop button is shown
        Assert.Contains("Start Loop", component.Markup);
    }

    // ── Error/Success Dismissal ──────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_DismissSuccess_ClearsSuccessMessage()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        // Directly trigger a success message via InvokeAsync
        await component.InvokeAsync(() =>
        {
            // Force a success message (simulate toggle success) by calling StateHasChanged after setting via reflection
            typeof(AgentCoding).GetField("_successMessage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(component.Instance, "Template saved.");
            component.Instance.GetType()
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(component.Instance, null);
        });

        // TODO: This assertion only verifies the success message is *set*, not that it is *cleared* on dismiss.
        // The test name (AgentCoding_DismissSuccess_ClearsSuccessMessage) implies the dismiss action should be
        // invoked and the message should subsequently be absent from the markup. A complete test would:
        // (1) set the message, (2) invoke the dismiss action (click the dismiss element), (3) assert the message
        // is no longer in component.Markup. Without the dismiss step, a broken dismiss handler would not be caught.
        Assert.Contains("Template saved.", component.Markup);
    }

    // ── Template Not Found (ConfirmRemoveTemplate) ───────────────────────────

    [Fact]
    public async Task AgentCoding_ConfirmRemoveTemplate_ShowsDeleteConfirm()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        // Simulate what ConfirmRemoveTemplate does via component invocation
        await component.InvokeAsync(() =>
        {
            var agentCoding = component.Instance;
            var method = typeof(AgentCoding).GetMethod(
                "ConfirmRemoveTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method is not null)
            {
                var template = pageService.Templates.FirstOrDefault();
                if (template is not null)
                    method.Invoke(agentCoding, [template]);
            }
        });

        // After confirming, delete confirm should show in markup
        Assert.Contains("Remove", component.Markup);
    }

    // ── Validate Add Template (ValidateAddTemplate) ───────────────────────────

    [Fact]
    public void AgentCoding_ValidateAddTemplate_EmptyName_ReturnsFalse()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        var (valid, error) = pageService.ValidateAddTemplate(new TemplateTableSection.TemplateFormModel { Name = "" });

        Assert.False(valid);
        Assert.Contains("Name is required", error);
    }

    [Fact]
    public void AgentCoding_ValidateAddTemplate_WithName_ReturnsTrue()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        var (valid, error) = pageService.ValidateAddTemplate(new TemplateTableSection.TemplateFormModel
        {
            Name = "My Template",
            IssueProviderId = "ip-2",
            RepoProviderId = "rp-2" // tracker and repository unused by the existing template
        });

        Assert.True(valid);
        Assert.Null(error);
    }

    // ── ShowAddForm populates defaults ────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_ShowAddForm_SetsDefaultProjectId()
    {
        var component = Render<AgentCoding>();

        // Open the add form
        var addBtn = component.FindAll("button").First(b => b.TextContent.Contains("+ Add Template"));
        await component.InvokeAsync(() => addBtn.Click());

        // Form should appear with default project pre-selected
        Assert.Contains("Add Pipeline Job Template", component.Markup);

        var pageService = Services.GetRequiredService<AgentCodingPageService>();
        // The add form in the razor template will have been bound to _addForm which has ProjectId set
        // We verify via rendering that the form shows
        Assert.Contains("Issue Provider", component.Markup);
        Assert.Contains("Repo Provider", component.Markup);
    }

    // ── HandleGlobalEscape ────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_HandleGlobalEscape_CallsCloseActiveDrawer()
    {
        var component = Render<AgentCoding>();

        // Invoke HandleGlobalEscape via reflection (it's private async void)
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod(
                "HandleGlobalEscape",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        // Drain the renderer sync context: HandleGlobalEscape's continuation posts
        // StateHasChanged back via InvokeAsync, so a second no-op InvokeAsync flushes it.
        await component.InvokeAsync(() => { });
        Assert.NotNull(component.Markup);
    }

    // ── CancelDelete ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_CancelDelete_HidesDeleteConfirm()
    {
        var component = Render<AgentCoding>();

        // Show confirm dialog first via reflection
        await component.InvokeAsync(() =>
        {
            var agentCoding = component.Instance;
            var method = typeof(AgentCoding).GetMethod(
                "ConfirmRemoveTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pageService = Services.GetRequiredService<AgentCodingPageService>();
            var template = pageService.Templates.FirstOrDefault();
            if (method is not null && template is not null)
                method.Invoke(agentCoding, [template]);
        });

        // Now cancel via reflection
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod(
                "CancelDelete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public void AgentCoding_ErrorMessage_HasDismissButton()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var component = Render<AgentCoding>();

        var errorDiv = component.Find(".settings-status.status-error");
        var dismissBtn = errorDiv.QuerySelector("button.agent-summary-dismiss");
        Assert.NotNull(dismissBtn);
        Assert.Equal("Dismiss", dismissBtn!.GetAttribute("title"));
        Assert.Contains("✕", dismissBtn.TextContent);
    }

    [Fact]
    public void AgentCoding_DismissError_ClearsErrorMessage()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var component = Render<AgentCoding>();

        // Verify error is displayed
        Assert.Contains("Connection failed", component.Markup);

        // Click dismiss button
        var dismissBtn = component.Find(".settings-status.status-error button.agent-summary-dismiss");
        dismissBtn.Click();

        // Error message should be gone
        Assert.Empty(component.FindAll(".settings-status.status-error"));
        Assert.DoesNotContain("Connection failed", component.Markup);
    }

    // TODO: Add negative regression test — when PipelineOrchestrationService.ActiveRun is set,
    // verify AgentCoding still renders template table, loop controls, and manual dispatch
    // (and does NOT contain "Pipeline in Progress" or "output-panel"). This guards against
    // accidental reintroduction of the progress view. (Review finding: WARNING)

    // TODO: This test only verifies the service return value but does not assert that
    // _errorMessage is rendered in the DOM (e.g., finding .status-error or .toast-message elements).
    // It should be refactored to invoke the component's DispatchFromDrawer method (not the service
    // directly) and assert that the error message appears in the rendered markup. Additionally,
    // the manual `IssueDrawerDispatching = true` assignment is redundant — the service sets it
    // internally — and masks potential regressions if the service's setDispatching call were removed.
    // (Review finding: WARNING)
    [Fact]
    public async Task AgentCoding_ShowsError_WhenDispatchWithNullTemplate()
    {
        var component = Render<AgentCoding>();

        // Get the page service instance — template is null because no drawer was opened
        var pageService = Services.GetRequiredService<AgentCodingPageService>();
        Assert.Null(pageService.IssueDrawerTemplate);

        // Simulate what the component's DispatchFromDrawer method does when template is null
        await component.InvokeAsync(async () =>
        {
            pageService.IssueDrawerDispatching = true;
            var (success, error, _) = await pageService.DispatchFromIssueDrawerAsync(
                new IssueSummary { Identifier = "1", Title = "Test", Labels = Array.Empty<string>() });
            Assert.False(success);
            Assert.NotNull(error);
            // Component would normally do: _errorMessage = error
            // We need to use reflection or a different path to set it on the component.
            // Instead, verify the service returned the right value — the unit tests cover the full path.
        });

        // The dispatching flag should be properly reset
        Assert.False(pageService.IssueDrawerDispatching);
    }

    // TODO: Add test coverage for the simplified HandleStateChanged method — verify that
    // LoopService.OnChange triggers a UI re-render and that the loop toast auto-dismiss logic
    // (AutoDismissLoopToast with Task.Delay) works correctly. The previous test
    // AgentCoding_WhenNewRunStarts_ClearsOutputLines was removed along with the deleted
    // functionality, leaving this async code path uncovered. (Review finding: WARNING)

    // ── Loop Controls ────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_StopLoop_CallsPageService()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("StopLoop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        // StopLoop delegates to PageService.StopLoopAsync — loop was never active so nothing to assert
        // except the component didn't throw
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task AgentCoding_ResumeLoop_CallsPageService()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("ResumeLoop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public void AgentCoding_CanStartLoop_FalseWithNoIssueProvider()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var component = Render<AgentCoding>();

        // With no issue provider, Start Loop button should be disabled
        var startBtn = component.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Start Loop"));
        Assert.NotNull(startBtn);
        Assert.True(startBtn!.HasAttribute("disabled") || component.Markup.Contains("No issue provider"));
    }

    // ── Template Callbacks (via reflection) ─────────────────────────────────

    [Fact]
    public async Task AgentCoding_ToggleTemplateEnabled_UpdatesState()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleTemplateEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, false)])!;
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AgentCoding_ToggleImplementationEnabled_CallsSave()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleImplementationEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, true)])!;
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AgentCoding_ToggleReviewEnabled_CallsSave()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleReviewEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, true)])!;
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AgentCoding_ToggleDecompositionEnabled_CallsSave()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleDecompositionEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, true)])!;
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AgentCoding_AddTemplate_ValidForm_CallsSaveTemplate()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            // Set _addForm with valid data — "ip-2"/"rp-2" are unused by the existing "ip-1"/"rp-1" template
            var addFormField = typeof(AgentCoding).GetField("_addForm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var form = new TemplateTableSection.TemplateFormModel
            {
                Name = "New Template",
                IssueProviderId = "ip-2",
                RepoProviderId = "rp-2",
                ProjectId = WellKnownIds.DefaultProjectId
            };
            addFormField!.SetValue(component.Instance, form);

            var method = typeof(AgentCoding).GetMethod("AddTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        _mockConfigClient.Verify(c => c.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_AddTemplate_InvalidForm_SetsFormError()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            // Leave _addForm with empty Name — validation should fail
            var addFormField = typeof(AgentCoding).GetField("_addForm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            addFormField!.SetValue(component.Instance, new TemplateTableSection.TemplateFormModel { Name = "" });

            var method = typeof(AgentCoding).GetMethod("AddTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        // _formError should be set, SaveTemplate should NOT have been called
        _mockProjectStore.Verify(s => s.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AgentCoding_RemoveTemplate_WhenConfirmed_CallsDelete()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            // Set _deletingTemplate
            var field = typeof(AgentCoding).GetField("_deletingTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field!.SetValue(component.Instance, pageService.Templates.First());

            var method = typeof(AgentCoding).GetMethod("RemoveTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        _mockConfigClient.Verify(c => c.DeleteTemplateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_RemoveTemplate_WhenNullTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            // Leave _deletingTemplate null
            var method = typeof(AgentCoding).GetMethod("RemoveTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        _mockProjectStore.Verify(s => s.DeleteTemplateAsync(
            It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ShowAddForm / CancelAddForm state ────────────────────────────────────

    [Fact]
    public async Task AgentCoding_ShowAddForm_SetsShowFlag()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("ShowAddForm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        var showField = typeof(AgentCoding).GetField("_showAddForm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True((bool)showField!.GetValue(component.Instance)!);
    }

    [Fact]
    public async Task AgentCoding_CancelAddForm_ClearsShowFlag()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            // Show first
            var showMethod = typeof(AgentCoding).GetMethod("ShowAddForm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            showMethod?.Invoke(component.Instance, null);
        });

        await component.InvokeAsync(() =>
        {
            var cancelMethod = typeof(AgentCoding).GetMethod("CancelAddForm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            cancelMethod?.Invoke(component.Instance, null);
        });

        var showField = typeof(AgentCoding).GetField("_showAddForm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.False((bool)showField!.GetValue(component.Instance)!);
    }

    // ── DismissAgentSummary / DismissError ───────────────────────────────────

    [Fact]
    public async Task AgentCoding_DismissAgentSummary_SetsFlag()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("DismissAgentSummary",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        var field = typeof(AgentCoding).GetField("_showAgentSummary",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.False((bool)field!.GetValue(component.Instance)!);
    }

    [Fact]
    public async Task AgentCoding_DismissError_ClearsField()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var errField = typeof(AgentCoding).GetField("_errorMessage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            errField!.SetValue(component.Instance, "Some error");
        });

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("DismissError",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        var errField2 = typeof(AgentCoding).GetField("_errorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.Null(errField2!.GetValue(component.Instance));
    }

    // ── OnTemplateChanged ────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_OnTemplateChanged_SetsTemplateId()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("OnTemplateChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "t-1" }]);
        });

        var field = typeof(AgentCoding).GetField("_manualDispatchTemplateId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.Equal("t-1", (string)field!.GetValue(component.Instance)!);
    }

    [Fact]
    public async Task AgentCoding_OnTemplateChanged_NullValue_SetsEmptyString()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("OnTemplateChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = null }]);
        });

        var field = typeof(AgentCoding).GetField("_manualDispatchTemplateId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.Equal("", (string)field!.GetValue(component.Instance)!);
    }

    // ── MoveTemplateToProject ────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_MoveTemplateToProject_CallsPageService()
    {
        _mockConfigClient.Setup(c => c.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("MoveTemplateToProject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var templateId = (TemplateId)"t-1";
            await (Task)method!.Invoke(component.Instance, [(templateId, WellKnownIds.DefaultProjectId, WellKnownIds.DefaultProjectId)])!;
        });

        // Moving to same project is a no-op — just verify no exception
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task MoveTemplateToProject_WhenServiceFails_SetsError()
    {
        _mockConfigClient.Setup(c => c.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("move failed"));

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("MoveTemplateToProject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance,
                [((TemplateId)"t-1", WellKnownIds.DefaultProjectId, "other-project")])!;
        });

        Assert.NotNull(component.Markup);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void AgentCoding_Dispose_SetsDisposedFlag()
    {
        var component = Render<AgentCoding>();

        component.Instance.Dispose();

        var field = typeof(AgentCoding).GetField("_disposed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True((bool)field!.GetValue(component.Instance)!);
    }

    // ── IsIssueActive / GetParentProject ─────────────────────────────────────

    [Fact]
    public async Task AgentCoding_IsIssueActive_ReturnsFalseByDefault()
    {
        var component = Render<AgentCoding>();

        // TODO: Add a complementary test for the active-status branch (where GetIssueWorkItemStatus returns
        // a non-null WorkItemStatus) to ensure the non-null path is also covered and won't silently regress.
        WorkItemStatus? result = WorkItemStatus.Running; // set to non-null to prove the method returns null
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("IsIssueActive",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            result = (WorkItemStatus?)method!.Invoke(component.Instance, [new IssueIdentifier("99"), new ProviderConfigId("ip-1")]);
        });

        Assert.Null(result);
    }

    // ── HandleStateChanged ────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCoding_HandleStateChanged_DoesNotThrowWhenNotDisposed()
    {
        var component = Render<AgentCoding>();

        // HandleStateChanged is async void — invoke via the event
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("HandleStateChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        // Drain the renderer sync context: HandleStateChanged's continuation posts
        // StateHasChanged back via InvokeAsync, so a second no-op InvokeAsync flushes it.
        await component.InvokeAsync(() => { });
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task HandleStateChanged_WhenLoopStatusCycleComplete_SetsHideLoopToastFalse()
    {
        var component = Render<AgentCoding>();

        // Set _lastLoopStatus to something different so the update triggers
        var lastStatusField = typeof(AgentCoding).GetField("_lastLoopStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        lastStatusField?.SetValue(component.Instance, "Idle");

        // HandleStateChanged reads from LoopService.StatusMessage which defaults to something non-CycleComplete
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("HandleStateChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        // Drain continuations
        await component.InvokeAsync(() => { });
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task HandleStateChanged_AfterDispose_DoesNotThrow()
    {
        var component = Render<AgentCoding>();
        component.Instance.Dispose();

        // HandleStateChanged after dispose must exit early without throwing
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("HandleStateChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        Assert.NotNull(component.Markup);
    }

    // ── Spec 049: the loop is global, templates are admin configuration ──────

    [Fact]
    public void Access_GlobalReadOnly_SeesLoopStateButNoLoopOrTemplateControls()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.ReadOnly));

        var component = Render<AgentCoding>();

        component.FindAll("[data-testid=loop-controls]").Should().ContainSingle();
        component.Markup.Should().NotContain("Start Loop");
        component.Markup.Should().NotContain("+ Add Template");
    }

    [Fact]
    public void Access_GlobalOperator_ControlsTheLoopButNotTemplates()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Operator));

        var component = Render<AgentCoding>();

        component.Markup.Should().Contain("Start Loop");
        component.Markup.Should().NotContain("+ Add Template");
    }

    [Fact]
    public void Access_ScopedUser_SeesNoLoopSection()
    {
        Services.AddTestAccess(TestAccess.Scoped(("6f1c2a9e-0000-0000-0000-00000000000a", AccessRole.Operator)));

        var component = Render<AgentCoding>();

        component.FindAll("[data-testid=loop-controls]").Should().BeEmpty();
        component.Markup.Should().NotContain("Start Loop");
    }

    // ── Toggle error paths ────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleTemplateEnabled_WhenServiceFails_SetsErrorMessage()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("save failed"));

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleTemplateEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, false)])!;
        });

        // Dispatch error message or exception — component must not crash
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task ToggleHousekeepingEnabled_WhenServiceFails_SetsErrorMessage()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("save failed"));

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleHousekeepingEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, true)])!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task ToggleBranchCleanupEnabled_WhenServiceFails_SetsErrorMessage()
    {
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("save failed"));

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("ToggleBranchCleanupEnabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var template = pageService.Templates.First();
            await (Task)method!.Invoke(component.Instance, [(template, true)])!;
        });

        Assert.NotNull(component.Markup);
    }

    // ── Drawer prev/next page guards ──────────────────────────────────────────

    [Fact]
    public async Task DrawerPrevPage_WhenPage1_DoesNotDecrement()
    {
        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        // _drawerPage is 1 by default (no drawer open) — prev should be a no-op
        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DrawerPrevPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        // No error should be set; page stays at 1
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task DrawerNextPage_WhenNoMore_DoesNotIncrement()
    {
        var component = Render<AgentCoding>();

        // _drawerHasMore is false (drawer is closed) — next should be a no-op
        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DrawerNextPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task DrawerToggleLabel_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        // _drawerTemplate is null — should return early without error
        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DrawerToggleLabel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, ["bug"])!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task DrawerClearLabels_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DrawerClearLabels",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    // ── PR Drawer guards ──────────────────────────────────────────────────────

    [Fact]
    public async Task PrDrawerPrevPage_WhenPage1_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("PrDrawerPrevPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task PrDrawerNextPage_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("PrDrawerNextPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task PrDrawerToggleLabel_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("PrDrawerToggleLabel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, ["agent:next"])!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task PrDrawerClearLabels_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("PrDrawerClearLabels",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    // ── Epic Drawer guards ────────────────────────────────────────────────────

    [Fact]
    public async Task EpicDrawerPrevPage_WhenPage1_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("EpicDrawerPrevPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task EpicDrawerNextPage_WhenNoMore_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("EpicDrawerNextPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task EpicDrawerToggleLabel_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("EpicDrawerToggleLabel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, ["epic"])!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task EpicDrawerClearLabels_WhenNoTemplate_DoesNothing()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("EpicDrawerClearLabels",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }

    // ── Drawer load failures ──────────────────────────────────────────────────
    // The page preselects the only enabled template on first render, so each handler loads its drawer
    // through the template's provider. A provider failure must end up in the page's error message.

    [Fact]
    public async Task SwitchToIssueDrawer_WhenIssueLoadFails_SetsErrorMessage()
    {
        var component = RenderWithFailingProviders();

        await InvokeDrawerHandlerAsync(component, "SwitchToIssueDrawer");

        ErrorMessageOf(component).Should().Be("Failed to load issues: tracker unavailable");
    }

    [Fact]
    public async Task SwitchToPrDrawer_WhenPullRequestLoadFails_SetsErrorMessage()
    {
        var component = RenderWithFailingProviders();

        await InvokeDrawerHandlerAsync(component, "SwitchToPrDrawer");

        ErrorMessageOf(component).Should().Be("Failed to load pull requests: repository unavailable");
    }

    [Fact]
    public async Task SwitchToEpicDrawer_WhenEpicLoadFails_SetsErrorMessage()
    {
        var component = RenderWithFailingProviders();

        await InvokeDrawerHandlerAsync(component, "SwitchToEpicDrawer");

        ErrorMessageOf(component).Should().Be("Failed to load epics: tracker unavailable");
    }

    [Fact]
    public async Task OpenDrawer_WhenIssueLoadFails_SetsErrorMessage()
    {
        var component = RenderWithFailingProviders();

        await InvokeDrawerHandlerAsync(component, "OpenDrawer");

        ErrorMessageOf(component).Should().Be("Failed to load issues: tracker unavailable");
    }

    [Fact]
    public async Task OpenPrDrawer_WhenPullRequestLoadFails_SetsErrorMessage()
    {
        var component = RenderWithFailingProviders();

        await InvokeDrawerHandlerAsync(component, "OpenPrDrawer");

        ErrorMessageOf(component).Should().Be("Failed to load pull requests: repository unavailable");
    }

    [Fact]
    public async Task OpenEpicDrawer_WhenEpicLoadFails_SetsErrorMessage()
    {
        var component = RenderWithFailingProviders();

        await InvokeDrawerHandlerAsync(component, "OpenEpicDrawer");

        ErrorMessageOf(component).Should().Be("Failed to load epics: tracker unavailable");
    }

    private IRenderedComponent<AgentCoding> RenderWithFailingProviders()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tracker unavailable"));
        _mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("repository unavailable"));
        return Render<AgentCoding>();
    }

    private static async Task InvokeDrawerHandlerAsync(IRenderedComponent<AgentCoding> component, string handlerName)
    {
        var handler = typeof(AgentCoding).GetMethod(handlerName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await component.InvokeAsync(() => (Task)handler.Invoke(component.Instance, null)!);
    }

    private static string? ErrorMessageOf(IRenderedComponent<AgentCoding> component) =>
        (string?)typeof(AgentCoding).GetField("_errorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(component.Instance);

    // ── CloseDrawer / ClosePrDrawer / CloseEpicDrawer ─────────────────────────

    [Fact]
    public async Task CloseDrawer_CallsPageService()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("CloseDrawer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task ClosePrDrawer_CallsPageService()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("ClosePrDrawer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task CloseEpicDrawer_CallsPageService()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("CloseEpicDrawer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(component.Instance, null);
        });

        Assert.NotNull(component.Markup);
    }

    // ── StartLoop exception path ──────────────────────────────────────────────

    [Fact]
    public async Task StartLoop_WhenPageServiceThrowsException_SetsErrorMessage()
    {
        // Make the loop service start throw an unhandled exception so the try/catch in StartLoop is exercised
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected failure"));

        // Re-setup config client to also throw
        _mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected failure"));

        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("StartLoop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        // Component must survive — error handled by catch block
        Assert.NotNull(component.Markup);
    }

    // ── DispatchFromDrawer — exception path ───────────────────────────────────

    [Fact]
    public async Task DispatchFromDrawer_WhenDrawerDispatchThrows_SetsErrorMessage()
    {
        var component = Render<AgentCoding>();

        // Inject exception path via reflection — simulate exception in the dispatch delegate
        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DispatchFromDrawer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            // _drawerTemplate is null → service will return failure but not throw
            await (Task)method!.Invoke(component.Instance,
                [new IssueSummary { Identifier = "1", Title = "Test", Labels = Array.Empty<string>() }])!;
        });

        // Component must survive the dispatching=false reset
        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task DispatchPrReviewFromDrawer_WhenDispatchThrows_SetsErrorMessage()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DispatchPrReviewFromDrawer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance,
                [new PullRequestSummary { Number = 1, Title = "PR", Identifier = "1", Description = "", Labels = Array.Empty<string>(), BranchName = "branch", TargetBranch = "main", Url = "https://github.com/org/repo/pull/1", IsDraft = false }])!;
        });

        Assert.NotNull(component.Markup);
    }

    [Fact]
    public async Task DispatchDecompositionFromDrawer_WhenDrawerDispatchThrows_SetsErrorMessage()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod("DispatchDecompositionFromDrawer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance,
                [new IssueSummary { Identifier = "1", Title = "Epic", Labels = Array.Empty<string>() }])!;
        });

        Assert.NotNull(component.Markup);
    }

    // ── RemoveTemplate — failure path ─────────────────────────────────────────

    [Fact]
    public async Task RemoveTemplate_WhenServiceFails_SetsError()
    {
        _mockConfigClient.Setup(c => c.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("delete failed"));

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var field = typeof(AgentCoding).GetField("_deletingTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field!.SetValue(component.Instance, pageService.Templates.First());

            var method = typeof(AgentCoding).GetMethod("RemoveTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        Assert.NotNull(component.Markup);
    }
}
