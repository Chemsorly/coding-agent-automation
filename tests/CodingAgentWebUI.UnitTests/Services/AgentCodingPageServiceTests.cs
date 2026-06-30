using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

public class AgentCodingPageServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;
    private readonly Mock<IDependencyChecker> _mockDependencyChecker;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly PipelineLoopService _loopService;
    private readonly AgentCodingPageService _service;

    public AgentCodingPageServiceTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockProjectStore = new Mock<IProjectStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockDependencyChecker = new Mock<IDependencyChecker>();

        var mockLogger = new Mock<Serilog.ILogger>();

        // PipelineLoopService requires a non-null PipelineOrchestrationService.
        // Create a minimal real instance with mocked dependencies.
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());
        var orchestration = new PipelineOrchestrationService(
            _mockConfigStore.Object, _mockProviderFactory.Object,
            new IssueDescriptionParser(), new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), new CiLogWriter(mockLogger.Object), new FeedbackService(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object);

        _loopService = new PipelineLoopService(
            orchestration, _mockProviderFactory.Object, _mockConfigStore.Object,
            _mockConfigStore.Object, _mockConfigStore.Object, mockLogger.Object);

        var mockAgentRegistry = new AgentRegistryService(mockLogger.Object);
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();
        _service = new AgentCodingPageService(
            _loopService, _mockWorkDistributor.Object, mockAgentRegistry, _mockConfigStore.Object,
            _mockProjectStore.Object, _mockProviderFactory.Object, _mockDependencyChecker.Object,
            _mockDispatchOrchestration.Object);
    }

    private static ProviderConfig MakeProvider(string id, ProviderKind kind = ProviderKind.Issue) =>
        new() { Id = id, Kind = kind, ProviderType = "GitHub", DisplayName = "Test" };

    private static PipelineJobTemplate MakeTemplate(string id = "t-1", string name = "Test") =>
        new() { Id = id, Name = name, IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeIssue(string id = "42", string title = "Test Issue") =>
        new() { Identifier = id, Title = title, Labels = Array.Empty<string>() };

    [Fact]
    public async Task InitializeAsync_LoadsAllConfiguration()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("ip-1") });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("rp-1", ProviderKind.Repository) });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MaxRetries = 5 });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { MakeTemplate() });
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var error = await _service.InitializeAsync();

        Assert.Null(error);
        Assert.Single(_service.Templates);
        Assert.Equal(5, _service.MaxRetries);
        Assert.Single(_service.IssueProviders);
        Assert.Single(_service.RepoProviders);
    }

    [Fact]
    public async Task InitializeAsync_ReturnsError_WhenExceptionThrown()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection failed"));

        var error = await _service.InitializeAsync();

        Assert.Equal("Failed to load configuration: connection failed", error);
    }

    [Fact]
    public async Task ToggleTemplateEnabledAsync_UpdatesTemplateInList()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (success, error) = await _service.ToggleTemplateEnabledAsync(template, false);

        Assert.True(success);
        Assert.Null(error);
        Assert.False(_service.Templates[0].Enabled);
    }

    [Fact]
    public async Task ToggleTemplateEnabledAsync_ReturnsError_WhenSaveFails()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("disk full"));

        var (success, error) = await _service.ToggleTemplateEnabledAsync(template, false);

        Assert.False(success);
        Assert.Equal("Failed to save: disk full", error);
    }

    [Fact]
    public void ValidateAddTemplate_RejectsEmptyName()
    {
        var form = new TemplateTableSection.TemplateFormModel { Name = "", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

        var (valid, formError) = _service.ValidateAddTemplate(form);

        Assert.False(valid);
        Assert.Equal("Name is required.", formError);
    }

    [Fact]
    public void ValidateAddTemplate_RejectsDuplicateProviderCombination()
    {
        _service.Templates.Add(MakeTemplate());
        var form = new TemplateTableSection.TemplateFormModel { Name = "New", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

        var (valid, formError) = _service.ValidateAddTemplate(form);

        Assert.False(valid);
        Assert.Contains("already exists", formError);
    }

    [Fact]
    public async Task AddTemplateAsync_AddsTemplateAndReloadsProjects()
    {
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        var form = new TemplateTableSection.TemplateFormModel { Name = "New Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

        var (success, error, msg) = await _service.AddTemplateAsync(form);

        Assert.True(success);
        Assert.Null(error);
        Assert.Contains("New Template", msg);
        Assert.Single(_service.Templates);
    }

    [Fact]
    public async Task RemoveTemplateAsync_RemovesAndReloadsProjects()
    {
        var template = MakeTemplate("t-1", "Removable");
        _service.Templates.Add(template);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), "t-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

        var (success, error, msg) = await _service.RemoveTemplateAsync(template);

        Assert.True(success);
        Assert.Empty(_service.Templates);
        Assert.Contains("Removable", msg);
    }

    [Fact]
    public async Task DispatchIssueAsync_ReturnsError_WhenNoAgents()
    {
        // In DB mode with IDispatchOrchestrationService injected, dispatch goes through
        // PrepareDistributionRequestAsync which builds a complete request with ProviderConfigs.
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));

        _mockDependencyChecker.Setup(d => d.CheckAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IIssueProvider>(),
            It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        // Setup orchestration to return a full request with ProviderConfigs populated
        var fullRequest = new JobDistributionRequest
        {
            IssueIdentifier = "42",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet,kiro",
            TimeoutSeconds = 7200,
            ProviderConfigs = new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" },
                new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Agent" }
            }
        };
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(
            "42", "ip-1", "rp-1", null, null, "manual",
            It.IsAny<PipelineProject>(),
            It.IsAny<WorkItemTaskType>(), It.IsAny<PipelineRunType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRequest);

        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "work-item-1", null));

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), template);

        Assert.True(success);

        // Verify that distribute was called with the ORCHESTRATED request (has ProviderConfigs)
        _mockWorkDistributor.Verify(w => w.DistributeAsync(
            It.Is<JobDistributionRequest>(r => r.ProviderConfigs != null && r.ProviderConfigs.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchIssueAsync_ReturnsError_WhenOrchestrationFails()
    {
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));

        _mockDependencyChecker.Setup(d => d.CheckAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IIssueProvider>(),
            It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        // Orchestration returns null (config not found, etc.)
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineProject>(),
            It.IsAny<WorkItemTaskType>(), It.IsAny<PipelineRunType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobDistributionRequest?)null);

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), template);

        Assert.False(success);
        Assert.Contains("orchestration preparation failed", error);
        _mockWorkDistributor.Verify(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchIssueAsync_ReturnsError_WhenProvidersMissing()
    {
        var template = MakeTemplate();
        // IssueProviders is empty — provider doesn't exist

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), template);

        Assert.False(success);
        Assert.Contains("no longer exist", error);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_SetsStateAndReturnsNull_OnSuccess()
    {
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 15, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary> { MakeIssue("1") }, HasMore = true, Page = 1, PageSize = 15 });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Null(error);
        Assert.Single(_service.DrawerIssues);
        Assert.True(_service.DrawerHasMore);
        Assert.False(_service.DrawerLoading);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_ReturnsError_WhenProviderNotFound()
    {
        var template = MakeTemplate("t-1", "T") with { IssueProviderId = "missing" };

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Equal("Issue provider not found for this template.", error);
    }

    [Fact]
    public async Task StopLoopAsync_StopsAndPersistsConfig()
    {
        _mockConfigStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.StopLoopAsync();

        _mockConfigStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_PopulatesReadiness_ForLoadedIssues()
    {
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 15, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "10", Title = "Issue 10", Labels = [], Description = "Blocked by #5" },
                    new() { Identifier = "11", Title = "Issue 11", Labels = [], Description = "No deps here" }
                },
                HasMore = false, Page = 1, PageSize = 15
            });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var blockedResult = new DependencyCheckResult { IsReady = false, BlockedBy = [5], TotalDependencies = 1 };
        var readyResult = DependencyCheckResult.NoDependencies;

        _mockDependencyChecker.Setup(d => d.CheckAsync("10", "Blocked by #5", It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blockedResult);
        _mockDependencyChecker.Setup(d => d.CheckAsync("11", "No deps here", It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readyResult);

        await _service.LoadDrawerIssuesAsync(template, 1);
        await _service.CheckDrawerDependenciesAsync(template);

        Assert.Equal(2, _service.DrawerReadiness.Count);
        Assert.False(_service.DrawerReadiness["10"].IsReady);
        Assert.Equal(new[] { 5 }, _service.DrawerReadiness["10"].BlockedBy);
        Assert.True(_service.DrawerReadiness["11"].IsReady);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_ReturnsGracefully_WhenProviderNotFound()
    {
        var template = MakeTemplate() with { IssueProviderId = "missing" };

        await _service.CheckDrawerDependenciesAsync(template);

        Assert.Empty(_service.DrawerReadiness);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_InvokesOnProgress_PerIssue()
    {
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 15, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "A", Labels = [] },
                    new() { Identifier = "2", Title = "B", Labels = [] }
                },
                HasMore = false, Page = 1, PageSize = 15
            });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        await _service.LoadDrawerIssuesAsync(template, 1);

        int progressCount = 0;
        await _service.CheckDrawerDependenciesAsync(template, () => progressCount++);

        Assert.Equal(2, progressCount);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_PartialResults_OnProviderException()
    {
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 15, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "A", Labels = [] },
                    new() { Identifier = "2", Title = "B", Labels = [] }
                },
                HasMore = false, Page = 1, PageSize = 15
            });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var callCount = 0;
        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, IIssueProvider, Dictionary<int, bool>, CancellationToken>((id, _, _, _, _) =>
            {
                if (++callCount == 2) throw new InvalidOperationException("provider error");
                return Task.FromResult(DependencyCheckResult.NoDependencies);
            });

        await _service.LoadDrawerIssuesAsync(template, 1);
        await _service.CheckDrawerDependenciesAsync(template);

        // First issue result stored, second one failed — partial results preserved
        Assert.Single(_service.DrawerReadiness);
        Assert.True(_service.DrawerReadiness["1"].IsReady);
    }

    [Fact]
    public async Task DispatchPrReviewAsync_DbMode_UsesOrchestration()
    {
        // DispatchPrReviewAsync must route through IDispatchOrchestrationService in DB mode,
        // otherwise the agent receives no ProviderConfigs and no RunId → token refresh fails.
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));

        var fullRequest = new JobDistributionRequest
        {
            IssueIdentifier = "pr-5",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "dotnet,kiro",
            TimeoutSeconds = 7200,
            RunId = Guid.NewGuid().ToString(),
            ProviderConfigs = new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
            }
        };

        _mockDispatchOrchestration.Setup(d => d.PrepareReviewDistributionRequestAsync(
            It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRequest);

        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "work-1", null));

        var pr = new PullRequestSummary { Identifier = "5", Title = "PR", BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Number = 5, Description = "", Labels = [], IsDraft = false };
        var (success, _, _) = await _service.DispatchPrReviewAsync(pr, template);

        Assert.True(success);

        // Verify orchestration was used (not direct minimal request)
        _mockDispatchOrchestration.Verify(d => d.PrepareReviewDistributionRequestAsync(
            It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify distributor received the orchestrated request with ProviderConfigs
        _mockWorkDistributor.Verify(w => w.DistributeAsync(
            It.Is<JobDistributionRequest>(r => r.ProviderConfigs != null && r.ProviderConfigs.Count > 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_DbMode_UsesOrchestration()
    {
        // DispatchDecompositionAsync must route through IDispatchOrchestrationService in DB mode.
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));

        var fullRequest = new JobDistributionRequest
        {
            IssueIdentifier = "epic-1",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Decomposition,
            AgentSelector = "dotnet,kiro",
            TimeoutSeconds = 900,
            RunId = Guid.NewGuid().ToString(),
            ProviderConfigs = new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
            }
        };

        _mockDispatchOrchestration.Setup(d => d.PrepareDecompositionDistributionRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineProject>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRequest);

        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "work-1", null));

        var issue = new IssueSummary { Identifier = "epic-1", Title = "Epic", Labels = new[] { "agent:epic" } };
        var (success, _, _) = await _service.DispatchDecompositionAsync(issue, template);

        Assert.True(success);

        // Verify orchestration was used
        _mockDispatchOrchestration.Verify(d => d.PrepareDecompositionDistributionRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineProject>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify distributor received the orchestrated request with ProviderConfigs
        _mockWorkDistributor.Verify(w => w.DistributeAsync(
            It.Is<JobDistributionRequest>(r => r.ProviderConfigs != null && r.ProviderConfigs.Count > 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Bug fix regression tests ──

    [Fact]
    public async Task LoadDrawerLabelsAsync_PopulatesDrawerLabels_WhenProviderReturnsLabels()
    {
        // Regression: fire-and-forget label load must populate DrawerLabels so UI can render them.
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "bug", "enhancement", "agent:next" });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var error = await _service.LoadDrawerLabelsAsync(template);

        Assert.Null(error);
        Assert.Equal(3, _service.DrawerLabels.Count);
        Assert.Contains("bug", _service.DrawerLabels);
        Assert.Contains("enhancement", _service.DrawerLabels);
    }

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_DeduplicatesIssuesWithBothEpicLabels()
    {
        // Regression: issues with both agent:epic AND agent:epic-approved appear in both
        // API queries. The result must be deduplicated by Identifier.
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));

        var duplicateIssue = new IssueSummary
        {
            Identifier = "100", Title = "Shared Epic",
            Labels = new[] { "agent:epic", "agent:epic-approved" }
        };
        var epicOnlyIssue = new IssueSummary
        {
            Identifier = "101", Title = "Analysis Epic",
            Labels = new[] { "agent:epic" }
        };
        var approvedOnlyIssue = new IssueSummary
        {
            Identifier = "102", Title = "Approved Epic",
            Labels = new[] { "agent:epic-approved" }
        };

        var mockIssueProvider = new Mock<IIssueProvider>();
        // First call (agent:epic labels) returns the duplicate + epic-only
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 8,
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains("agent:epic")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary> { duplicateIssue, epicOnlyIssue },
                HasMore = false, Page = 1, PageSize = 8
            });
        // Second call (agent:epic-approved labels) returns the duplicate + approved-only
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 8,
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains("agent:epic-approved")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary> { duplicateIssue, approvedOnlyIssue },
                HasMore = false, Page = 1, PageSize = 8
            });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var error = await _service.LoadEpicDrawerIssuesAsync(template, 1);

        Assert.Null(error);
        // Should have 3 unique issues, NOT 4 (duplicate counted once)
        Assert.Equal(3, _service.EpicDrawerIssues.Count);
        Assert.Equal(1, _service.EpicDrawerIssues.Count(i => i.Identifier == "100"));
    }

    [Fact]
    public async Task LoadPrDrawerPageAsync_ClearsPrState_WhenCalledAfterPreviousLoad()
    {
        // Regression: ClosePrDrawer must clear PrDrawerPrs, PrDrawerPage, PrDrawerHasMore
        // so stale data doesn't flash on next open.
        var template = MakeTemplate();
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(1, 15, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>
                {
                    new() { Identifier = "10", Title = "PR 10", BranchName = "feat/a", TargetBranch = "main", Url = "http://x", Number = 10, Description = "", Labels = [], IsDraft = false }
                },
                HasMore = true, Page = 1, PageSize = 15
            });
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        // Load PRs
        await _service.LoadPrDrawerPageAsync(template, 1);
        Assert.Single(_service.PrDrawerPrs);
        Assert.True(_service.PrDrawerHasMore);

        // Simulate close — this should clear all PR state
        _service.ClearPrDrawerLabelFilter();
        // Currently only clears labels, NOT the PR list — this assertion should FAIL before the fix
        Assert.Empty(_service.PrDrawerPrs);
        Assert.Equal(1, _service.PrDrawerPage);
        Assert.False(_service.PrDrawerHasMore);
    }
}
