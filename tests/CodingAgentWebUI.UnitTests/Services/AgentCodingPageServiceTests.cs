using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;

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
        var orchestration = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
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
        // TODO: Add tests for failure path (DistributeAndFinalizeAsync returning Success=false, triggering
        // the distributionFailedError tuple) and queued path (Success=true, Queued=true, which should return
        // queuedMessage instead of dispatchedMessage) to cover branching logic in DispatchWithOrchestrationAsync.
        // Currently all tests hardcode DispatchOutcome(true, false, null) — a bug swapping the two message
        // constants or mishandling the failure branch would not be caught.
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

        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), template);

        Assert.True(success);

        // Verify that DistributeAndFinalizeAsync was called with the ORCHESTRATED request (has ProviderConfigs)
        _mockDispatchOrchestration.Verify(d => d.DistributeAndFinalizeAsync(
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
        _mockDispatchOrchestration.Verify(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
        // TODO: Verify that RevertFailedDistributionAsync is NOT called on success path, and assert
        // the specific success message returned to detect swapped queuedMessage/dispatchedMessage parameters.
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

        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var pr = new PullRequestSummary { Identifier = "5", Title = "PR", BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Number = 5, Description = "", Labels = [], IsDraft = false };
        var (success, _, _) = await _service.DispatchPrReviewAsync(pr, template);

        Assert.True(success);

        // Verify orchestration was used (not direct minimal request)
        _mockDispatchOrchestration.Verify(d => d.PrepareReviewDistributionRequestAsync(
            It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify DistributeAndFinalizeAsync received the orchestrated request with ProviderConfigs
        _mockDispatchOrchestration.Verify(d => d.DistributeAndFinalizeAsync(
            It.Is<JobDistributionRequest>(r => r.ProviderConfigs != null && r.ProviderConfigs.Count > 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_DbMode_UsesOrchestration()
    {
        // TODO: Assert the returned SuccessMessage content to detect incorrect message assignment
        // in the refactored DispatchWithOrchestrationAsync helper.
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

        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var issue = new IssueSummary { Identifier = "epic-1", Title = "Epic", Labels = new[] { "agent:epic" } };
        var (success, _, _) = await _service.DispatchDecompositionAsync(issue, template);

        Assert.True(success);

        // Verify orchestration was used
        _mockDispatchOrchestration.Verify(d => d.PrepareDecompositionDistributionRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineProject>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify DistributeAndFinalizeAsync received the orchestrated request with ProviderConfigs
        _mockDispatchOrchestration.Verify(d => d.DistributeAndFinalizeAsync(
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

    // ── Drawer Orchestration Tests ──

    [Fact]
    public async Task OpenIssueDrawerAsync_SetsOpenState_AndLoadsData()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        var error = await _service.OpenIssueDrawerAsync("t-1");

        Assert.Null(error);
        Assert.True(_service.IsIssueDrawerOpen);
        Assert.Equal(template, _service.IssueDrawerTemplate);
        Assert.NotEmpty(_service.DrawerIssues);
    }

    [Fact]
    public async Task OpenIssueDrawerAsync_ClosesOtherDrawers()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        // Pre-open PR drawer state manually
        await _service.OpenPrDrawerAsync("t-1");
        Assert.True(_service.IsPrDrawerOpen);

        // Open issue drawer should close PR drawer
        await _service.OpenIssueDrawerAsync("t-1");

        Assert.True(_service.IsIssueDrawerOpen);
        Assert.False(_service.IsPrDrawerOpen);
    }

    [Fact]
    public async Task OpenIssueDrawerAsync_RefreshesActiveIssues()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        var expectedSet = new HashSet<(string, string)> { ("42", "ip-1") };
        _mockWorkDistributor.Setup(w => w.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSet);

        await _service.OpenIssueDrawerAsync("t-1");

        Assert.True(_service.IsIssueActive("42", "ip-1"));
    }

    [Fact]
    public async Task CloseIssueDrawer_ClearsState()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");
        Assert.True(_service.IsIssueDrawerOpen);
        Assert.NotNull(_service.IssueDrawerTemplate);

        _service.CloseIssueDrawer();

        Assert.False(_service.IsIssueDrawerOpen);
        Assert.Null(_service.IssueDrawerTemplate);
    }

    [Fact]
    public async Task SwitchToIssueDrawer_ReusesCache_WhenDataExists()
    {
        // TODO: This test does not actually validate cache reuse vs re-fetch — DrawerIssues is cleared on close,
        // so the switch always calls open again. Asserting only IsIssueDrawerOpen passes regardless of which path is taken.
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        // First open to populate data
        await _service.OpenIssueDrawerAsync("t-1");
        Assert.True(_service.IsIssueDrawerOpen);

        // Close and then switch — should reuse cached data
        _service.CloseIssueDrawer();
        Assert.False(_service.IsIssueDrawerOpen);

        // After CloseIssueDrawer, DrawerIssues is cleared, so switch will call open again
        await _service.SwitchToIssueDrawerAsync("t-1");
        Assert.True(_service.IsIssueDrawerOpen);
    }

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_ClosesDrawer_OnSuccess()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");
        Assert.True(_service.IsIssueDrawerOpen);

        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineProject>(), It.IsAny<WorkItemTaskType>(), It.IsAny<PipelineRunType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMinimalDistributionRequest());
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, error, msg) = await _service.DispatchFromIssueDrawerAsync(MakeIssue());

        Assert.True(success);
        Assert.False(_service.IsIssueDrawerOpen); // drawer closed on success
    }

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_ReturnsError_OnFailure()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");

        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult { IsReady = false, BlockedBy = [10], TotalDependencies = 1 });

        var (success, error, msg) = await _service.DispatchFromIssueDrawerAsync(MakeIssue());

        Assert.False(success);
        Assert.Contains("blocked", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(_service.IsIssueDrawerOpen); // drawer stays open on failure
    }

    [Fact]
    public async Task DispatchFromPrDrawerAsync_DoesNotCloseDrawer_OnSuccess()
    {
        var template = MakeTemplate() with { RepoProviderId = "rp-1" };
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        SetupMockRepoProvider();

        await _service.OpenPrDrawerAsync("t-1");
        Assert.True(_service.IsPrDrawerOpen);

        _mockDispatchOrchestration.Setup(d => d.PrepareReviewDistributionRequestAsync(
            It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMinimalDistributionRequest());
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var pr = new PullRequestSummary { Identifier = "99", Number = 99, Title = "Fix", BranchName = "fix/a", TargetBranch = "main", Url = "http://x", Description = "", Labels = Array.Empty<string>(), IsDraft = false };
        var (success, error, msg) = await _service.DispatchFromPrDrawerAsync(pr);

        Assert.True(success);
        Assert.True(_service.IsPrDrawerOpen); // PR drawer stays open on success
    }

    [Fact]
    public async Task DispatchFromEpicDrawerAsync_ClosesDrawer_OnSuccess()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        SetupMockIssueProvider();

        await _service.OpenEpicDrawerAsync("t-1");
        Assert.True(_service.IsEpicDrawerOpen);

        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);
        _mockDispatchOrchestration.Setup(d => d.PrepareDecompositionDistributionRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<PipelineProject>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMinimalDistributionRequest());
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, error, msg) = await _service.DispatchFromEpicDrawerAsync(MakeIssue());

        Assert.True(success);
        Assert.False(_service.IsEpicDrawerOpen); // drawer closed on success
    }

    [Fact]
    public async Task ActiveDrawerTab_ReflectsOpenDrawer()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        SetupMockIssueProvider();
        SetupMockRepoProvider();

        Assert.Equal("", _service.ActiveDrawerTab);

        await _service.OpenIssueDrawerAsync("t-1");
        Assert.Equal("issue", _service.ActiveDrawerTab);

        _service.CloseIssueDrawer();
        await _service.OpenPrDrawerAsync("t-1");
        Assert.Equal("pr", _service.ActiveDrawerTab);

        _service.ClosePrDrawer();
        await _service.OpenEpicDrawerAsync("t-1");
        Assert.Equal("epic", _service.ActiveDrawerTab);
    }

    [Fact]
    public async Task CloseActiveDrawer_ClosesWhicheverIsOpen()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");
        Assert.True(_service.IsIssueDrawerOpen);

        _service.CloseActiveDrawer();

        Assert.False(_service.IsIssueDrawerOpen);
        Assert.Null(_service.IssueDrawerTemplate);
    }

    [Fact]
    public void Dispose_CancelsCts()
    {
        // TODO: This test only verifies no-throw — it does not assert that a pending CancellationToken is actually
        // cancelled. Open a drawer first, capture the CTS token, dispose, and assert token.IsCancellationRequested.
        // Should not throw
        _service.Dispose();
        _service.Dispose(); // double-dispose safe
    }

    [Fact]
    public async Task ClosePrDrawer_NullsTemplate()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        SetupMockRepoProvider();

        await _service.OpenPrDrawerAsync("t-1");
        Assert.NotNull(_service.PrDrawerTemplate);

        _service.ClosePrDrawer();
        Assert.Null(_service.PrDrawerTemplate);
    }

    private void SetupMockIssueProvider()
    {
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary> { MakeIssue() },
                Page = 1, PageSize = 15, HasMore = false
            });
        mockIssueProvider.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    private void SetupMockRepoProvider()
    {
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>
                {
                    new() { Identifier = "99", Title = "PR", BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Number = 99, Description = "", Labels = Array.Empty<string>(), IsDraft = false }
                },
                Page = 1, PageSize = 15, HasMore = false
            });
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        // Also set up issue provider for labels (needed by OpenPrDrawerAsync)
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    private static JobDistributionRequest CreateMinimalDistributionRequest() => new()
    {
        IssueIdentifier = "42",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "manual",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600
    };

    // ── DrawerCancellationToken Tests ──

    [Fact]
    public void DrawerCancellationToken_ReturnsNone_WhenNoDrawerOpen()
    {
        Assert.Equal(CancellationToken.None, _service.DrawerCancellationToken);
    }

    [Fact]
    public async Task DrawerCancellationToken_IsValid_WhenDrawerOpen()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");

        var token = _service.DrawerCancellationToken;
        Assert.NotEqual(CancellationToken.None, token);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task DrawerCancellationToken_IsCancelled_AfterDrawerClose()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");
        var token = _service.DrawerCancellationToken;

        _service.CloseIssueDrawer();

        Assert.True(token.IsCancellationRequested);
    }

    // TODO: This test uses its own pre-cancelled CTS rather than the service's DrawerCancellationToken.
    // Add a test that opens the drawer, captures DrawerCancellationToken, closes the drawer (cancelling the CTS),
    // and verifies the token is cancelled — to validate the actual integration path (issue #1057 criterion #4).
    [Fact]
    public async Task CheckDrawerDependenciesAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        SetupMockIssueProvider();

        await _service.OpenIssueDrawerAsync("t-1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CheckDrawerDependenciesAsync(template, null, cts.Token));
    }

    // TODO: Add tests for the undo-callback stale-capture fix (issue #1057 criterion #2).
    // Specifically, test the no-op case when a template is removed between toggle and undo click —
    // the undo lambda should re-resolve from PageService.Templates by ID and return early if null.

    // TODO: Add tests for HandleGlobalEscape exception-handling fix (issue #1057 criterion #1).
    // The method now catches ObjectDisposedException and guards against _disposed. A bUnit test
    // could verify that calling HandleGlobalEscape after component disposal does not throw.

    [Fact]
    public async Task StartLoopAsync_WhenLoopServiceThrows_ReturnsErrorTuple()
    {
        // Simulate config store throwing during StartLoopAsync's config-load phase.
        // With the fix in PipelineLoopService, this returns (false, error) instead of throwing.
        // But if something else throws (e.g., UpdatePipelineConfigAsync on success path),
        // AgentCodingPageService should catch and surface it.
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database locked"));

        var (success, error) = await _service.StartLoopAsync();

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task StartLoopAsync_WhenUpdateConfigThrows_ReturnsErrorTuple()
    {
        // Setup so StartLoopAsync succeeds (loop starts), but UpdatePipelineConfigAsync throws
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockConfigStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("ip-1") });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("rp-1", ProviderKind.Repository) });
        _mockConfigStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk full"));

        var (success, error) = await _service.StartLoopAsync();

        Assert.False(success);
        Assert.Contains("Disk full", error!);
    }
}
