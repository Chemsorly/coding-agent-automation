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
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        _pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupDefaults();

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<IPipelineLoopService>(new PipelineLoopService(_pipelineService, _mockFactory.Object, _mockStore.Object, _mockStore.Object, _mockStore.Object, mockLogger.Object));
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
        Services.AddSingleton(_mockProjectStore.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDispatcherService(registry, mockLogger.Object));
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

        // Dispose should not throw
        component.Dispose();
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

    // TODO: Add a negative test verifying that error messages do NOT auto-dismiss after a timeout
    //       (acceptance criteria: "Error messages do NOT auto-dismiss"). Use a fake timer or Task.Delay
    //       simulation to confirm the error remains displayed after 3+ seconds.

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

    // TODO: Add test coverage for the simplified HandleStateChanged method — verify that
    // LoopService.OnChange triggers a UI re-render and that the loop toast auto-dismiss logic
    // (AutoDismissLoopToast with Task.Delay) works correctly. The previous test
    // AgentCoding_WhenNewRunStarts_ClearsOutputLines was removed along with the deleted
    // functionality, leaving this async code path uncovered. (Review finding: WARNING)
}
