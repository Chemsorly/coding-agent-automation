using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
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
    private readonly Mock<IJobDispatcher> _mockJobDispatcher;
    private readonly Mock<IDependencyChecker> _mockDependencyChecker;
    private readonly PipelineLoopService _loopService;
    private readonly AgentCodingPageService _service;

    public AgentCodingPageServiceTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockProjectStore = new Mock<IProjectStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockJobDispatcher = new Mock<IJobDispatcher>();
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

        _service = new AgentCodingPageService(
            _loopService, _mockJobDispatcher.Object, _mockConfigStore.Object,
            _mockProjectStore.Object, _mockProviderFactory.Object, _mockDependencyChecker.Object);
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
        var template = MakeTemplate();
        _service.IssueProviders.Add(MakeProvider("ip-1"));
        _service.RepoProviders.Add(MakeProvider("rp-1", ProviderKind.Repository));
        _mockJobDispatcher.Setup(d => d.HasRegisteredAgents).Returns(false);

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), template);

        Assert.False(success);
        Assert.Contains("no agents", error);
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
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(1, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary> { MakeIssue("1") }, HasMore = true, Page = 1, PageSize = 25 });
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
}
