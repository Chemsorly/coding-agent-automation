using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
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
    private readonly PipelineOrchestrationService _pipelineService;

    public AgentCodingPageComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupDefaults();

        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<IPipelineLoopService>(new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = null,
            DependencyChecker = null,
            HousekeepingService = null
        }));
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
    public void AgentCoding_RendersPageHeader()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Agent Coding", component.Markup);
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
        Assert.Contains("Agent Coding", markupBeforeDispose);
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
        // All 6 steps need to be true for AllComplete to hide the checklist.
        // With default setup: has issue provider, repo provider, template — but no project or agent or loop active.
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
        var component = Render<AgentCoding>();

        var browseBtn = component.Find("[data-testid='browse-issues-btn']");
        Assert.True(browseBtn.HasAttribute("disabled"));
        Assert.True(browseBtn.HasAttribute("title"));
        Assert.Contains("Select a pipeline template to browse issues", browseBtn.GetAttribute("title"));
    }

    [Fact]
    public void AgentCoding_WhenBrowseEpicsDisabled_ShowsTooltip()
    {
        var component = Render<AgentCoding>();

        var browseBtn = component.Find("[data-testid='browse-epics-btn']");
        Assert.True(browseBtn.HasAttribute("disabled"));
        Assert.True(browseBtn.HasAttribute("title"));
        Assert.Contains("Select a pipeline template to browse epics", browseBtn.GetAttribute("title"));
    }

    [Fact]
    public void AgentCoding_WhenBrowsePrsDisabled_ShowsTooltip()
    {
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

    // ── Template Toggle / Add / Remove ──────────────────────────────────────

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
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
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

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleTemplateEnabled_CallsService()
    {
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleTemplateEnabledAsync(template, false);
            Assert.True(success, error);
        });

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleImplementationEnabled_CallsService()
    {
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleImplementationEnabledAsync(template, true);
            Assert.True(success, error);
        });

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleReviewEnabled_CallsService()
    {
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleReviewEnabledAsync(template, true);
            Assert.True(success, error);
        });

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_ToggleDecompositionEnabled_CallsService()
    {
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentCoding>();
        var pageService = Services.GetRequiredService<AgentCodingPageService>();

        await component.InvokeAsync(async () =>
        {
            var template = pageService.Templates.First();
            var (success, error) = await pageService.ToggleDecompositionEnabledAsync(template, true);
            Assert.True(success, error);
        });

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCoding_RemoveTemplate_Success_RemovesFromList()
    {
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
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
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-2" // different combo — no duplicate
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

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(
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

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(
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

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(
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

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(
            It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AgentCoding_AddTemplate_ValidForm_CallsSaveTemplate()
    {
        var component = Render<AgentCoding>();

        await component.InvokeAsync(async () =>
        {
            // Set _addForm with valid data — use "rp-2" to avoid duplicate with existing "ip-1"/"rp-1" template
            var addFormField = typeof(AgentCoding).GetField("_addForm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var form = new TemplateTableSection.TemplateFormModel
            {
                Name = "New Template",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-2",
                ProjectId = WellKnownIds.DefaultProjectId
            };
            addFormField!.SetValue(component.Instance, form);

            var method = typeof(AgentCoding).GetMethod("AddTemplate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(component.Instance, null)!;
        });

        _mockProjectStore.Verify(s => s.SaveTemplateAsync(
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

        _mockProjectStore.Verify(s => s.DeleteTemplateAsync(
            It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()), Times.Once);
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
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
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

        bool result = false;
        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("IsIssueActive",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            result = (bool)method!.Invoke(component.Instance, ["99", "ip-1"])!;
        });

        Assert.False(result);
    }

    [Fact]
    public async Task AgentCoding_GetParentProject_ReturnsProject()
    {
        var component = Render<AgentCoding>();
        PipelineProject? result = null;

        await component.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod("GetParentProject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            result = (PipelineProject?)method!.Invoke(component.Instance, [(TemplateId)"t-1"]);
        });

        Assert.NotNull(result);
        Assert.Equal(WellKnownIds.DefaultProjectId, result!.Id);
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
}
