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
/// bUnit component tests for the AgentCoding page (Template Table UI).
/// Renders the actual Blazor component and asserts on markup and view switching.
/// </summary>
public class AgentCodingPageComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;
    private readonly PipelineOrchestrationService _pipelineService;

    public AgentCodingPageComponentTests()
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

        _pipelineService = new PipelineOrchestrationService(
            _mockStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object);

        SetupDefaults();

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton(new PipelineLoopService(_pipelineService, _mockFactory.Object, _mockStore.Object, _mockStore.Object, _mockStore.Object, mockLogger.Object));
        Services.AddSingleton(new Mock<IJSRuntime>().Object);

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        Services.AddSingleton(mockProjectStore.Object);

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
                    new() { Id = "t-1", Name = "DotNet Repo", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
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
                PipelineJobTemplates = new List<PipelineJobTemplate>()
            });

        var component = Render<AgentCoding>();

        Assert.Contains("No pipeline job templates configured", component.Markup);
    }

    [Fact]
    public void AgentCoding_WhenNoTemplates_StartLoopDisabled()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                PipelineJobTemplates = new List<PipelineJobTemplate>()
            });

        var component = Render<AgentCoding>();

        var startBtn = component.FindAll("button").First(b => b.TextContent.Contains("Start Loop"));
        Assert.True(startBtn.HasAttribute("disabled"));
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
                WorkspaceBaseDirectory = Path.GetTempPath(),
                PipelineJobTemplates = new List<PipelineJobTemplate>
                {
                    new() { Id = "t-1", Name = "Bad Template", IssueProviderId = "nonexistent", RepoProviderId = "rp-1", Enabled = true }
                }
            });

        var component = Render<AgentCoding>();

        // Should show warning indicator for missing provider
        Assert.Contains("⚠️", component.Markup);
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
    public async Task AgentCoding_WhenNewRunStarts_ClearsOutputLines()
    {
        var component = Render<AgentCoding>();

        // Simulate first run: set ActiveRun and fire events
        var run1 = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "1",
            IssueTitle = "First",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = PipelineStep.GeneratingCode,
            StartedAt = DateTime.UtcNow
        };
        SetActiveRun(run1);
        RaiseEvent(_pipelineService, "OnChange");
        RaiseEvent(_pipelineService, "OnOutputLine", "output from run 1");

        component.WaitForAssertion(() => Assert.Contains("output from run 1", component.Markup),
            timeout: TimeSpan.FromSeconds(5));

        // Simulate second run starting
        var run2 = new PipelineRun
        {
            RunId = "run-2",
            IssueIdentifier = "2",
            IssueTitle = "Second",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = PipelineStep.CloningRepository,
            StartedAt = DateTime.UtcNow
        };
        SetActiveRun(run2);
        RaiseEvent(_pipelineService, "OnChange");

        // Output from run 1 should be cleared
        component.WaitForAssertion(() => Assert.DoesNotContain("output from run 1", component.Markup),
            timeout: TimeSpan.FromSeconds(5));
    }

    private void SetActiveRun(PipelineRun run)
    {
        // ActiveRun delegates to lifecycle service — set via lifecycle's public setter
        var lifecycleField = typeof(PipelineOrchestrationService)
            .GetField("_lifecycle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var lifecycle = lifecycleField?.GetValue(_pipelineService) as PipelineRunLifecycleService;
        if (lifecycle != null)
            lifecycle.ActiveRun = run;
    }

    private void RaiseEvent(object target, string eventName, params object[] args)
    {
        // Events now live on the lifecycle service (accessed via _lifecycle field on orchestration)
        var lifecycleField = typeof(PipelineOrchestrationService)
            .GetField("_lifecycle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var lifecycle = lifecycleField?.GetValue(target);
        if (lifecycle == null) return;

        var field = lifecycle.GetType()
            .GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var del = field?.GetValue(lifecycle) as Delegate;
        del?.DynamicInvoke(args.Length > 0 ? args : []);
    }
}
