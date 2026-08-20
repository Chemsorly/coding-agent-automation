using Moq;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for AgentCodingPageService as a coordinator — verifies that template CRUD,
/// loop controls, and cross-drawer coordination work correctly.
/// Drawer-specific tests live in IssueDrawerServiceTests, PrReviewDrawerServiceTests,
/// and EpicDrawerServiceTests.
/// <para>
/// Spec 045: IConfigurationStore and IProjectStore replaced by IPipelineApiConfigClient.
/// </para>
/// </summary>
public class AgentCodingPageServiceTests
{
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient;
    private readonly Mock<IIssueDrawerService> _mockIssueDrawerService;
    private readonly Mock<IPrReviewDrawerService> _mockPrReviewDrawerService;
    private readonly Mock<IEpicDrawerService> _mockEpicDrawerService;
    private readonly PipelineLoopService _loopService;
    private readonly AgentCodingPageService _service;

    // Real drawer state services so property forwarding works in tests that check DrawerState
    private readonly DrawerStateService<IssueSummary> _issueDrawerState;
    private readonly DrawerStateService<PullRequestSummary> _prDrawerState;
    private readonly DrawerStateService<IssueSummary> _epicDrawerState;

    // Exposed so individual tests can add setups for PipelineLoopService's store dependencies.
    // PipelineLoopService still uses these old store interfaces (Spec 045 Task 6 used adapters via DI,
    // but tests construct PipelineLoopService directly with these mocks).
    private readonly Mock<IConfigurationStore> _mockLoopConfigStore;
    private readonly Mock<IProjectStore> _mockLoopProjectStore;

    public AgentCodingPageServiceTests()
    {
        _mockConfigClient = new Mock<IPipelineApiConfigClient>();
        _mockIssueDrawerService = new Mock<IIssueDrawerService>();
        _mockPrReviewDrawerService = new Mock<IPrReviewDrawerService>();
        _mockEpicDrawerService = new Mock<IEpicDrawerService>();

        // Wire up real DrawerStateService instances for property-forwarding tests
        _issueDrawerState = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((true, null, null)));
        _prDrawerState = new DrawerStateService<PullRequestSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((true, null, null)));
        _epicDrawerState = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((true, null, null)));

        _mockIssueDrawerService.Setup(s => s.DrawerState).Returns(_issueDrawerState);
        _mockIssueDrawerService.Setup(s => s.DrawerReadiness).Returns(new Dictionary<string, DependencyCheckResult>());
        _mockIssueDrawerService.Setup(s => s.ActiveIssues).Returns(new HashSet<(IssueIdentifier, ProviderConfigId)>());
        _mockPrReviewDrawerService.Setup(s => s.DrawerState).Returns(_prDrawerState);
        _mockEpicDrawerService.Setup(s => s.DrawerState).Returns(_epicDrawerState);

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // PipelineLoopService still uses the old store interfaces (Task 6 not done yet).
        // Use minimal mocks to satisfy constructor.
        _mockLoopConfigStore = new Mock<IConfigurationStore>();
        _mockLoopProjectStore = new Mock<IProjectStore>();
        var mockConfigStore = _mockLoopConfigStore;
        var mockProjectStore = _mockLoopProjectStore;

        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: mockConfigStore.Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            historyService: mockHistoryService.Object);

        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = runCreator,
            ProviderFactory = new Mock<IProviderFactory>().Object,
            PipelineConfigStore = mockConfigStore.Object,
            ProviderConfigStore = mockConfigStore.Object,
            ProjectStore = mockProjectStore.Object,
            Logger = mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = null,
            DependencyChecker = null,
            HousekeepingService = null,
            LeaderElection = null
        });

        _service = new AgentCodingPageService(
            _loopService,
            _mockConfigClient.Object,
            _mockIssueDrawerService.Object,
            _mockPrReviewDrawerService.Object,
            _mockEpicDrawerService.Object);
    }

    private static ProviderConfig MakeProvider(string id, ProviderKind kind = ProviderKind.Issue) =>
        new() { Id = id, Kind = kind, ProviderType = "GitHub", DisplayName = "Test" };

    private static PipelineJobTemplate MakeTemplate(string id = "t-1", string name = "Test") =>
        new() { Id = id, Name = name, IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeIssue(string id = "42", string title = "Test Issue") =>
        new() { Identifier = id, Title = title, Labels = Array.Empty<string>() };

    // ── InitializeAsync ──

    [Fact]
    public async Task InitializeAsync_LoadsAllConfiguration()
    {
        // TODO: [WARNING] InitializeAsync ends with PropagateProviderContext(), which uses concrete
        // type-casts (_mockIssueDrawerService is IssueDrawerService) that silently no-op against Moq
        // mocks. This test does not verify that provider context reached the drawer services after
        // initialization. A regression in PropagateProviderContext (wrong list, missing call) would not
        // be detected here. Dedicated integration-style tests using concrete drawer service instances
        // would be needed to validate end-to-end provider context propagation.
        _mockConfigClient.Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("ip-1") });
        _mockConfigClient.Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("rp-1", ProviderKind.Repository) });
        _mockConfigClient.Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockConfigClient.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MaxRetries = 5 });
        _mockConfigClient.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { MakeTemplate() });
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockConfigClient.Setup(s => s.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigClient.Setup(s => s.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockConfigClient.Setup(s => s.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
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
        _mockConfigClient.Setup(s => s.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection failed"));

        var error = await _service.InitializeAsync();

        Assert.Equal("Failed to load configuration: connection failed", error);
    }

    // ── Template Operations ──

    [Fact]
    public async Task ToggleTemplateEnabledAsync_UpdatesTemplateInList()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);
        _mockConfigClient.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
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
        _mockConfigClient.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
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
        _mockConfigClient.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
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
        _mockConfigClient.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), "t-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

        var (success, error, msg) = await _service.RemoveTemplateAsync(template);

        Assert.True(success);
        Assert.Empty(_service.Templates);
        Assert.Contains("Removable", msg);
    }

    [Fact]
    public async Task MoveTemplateToProjectAsync_MovesTemplateAndReloadsProjects()
    {
        var sourceProject = new PipelineProject { Id = "proj-src", Name = "Source", TemplateIds = new List<string> { "t-1", "t-2" } };
        var targetProject = new PipelineProject { Id = "proj-tgt", Name = "Target", TemplateIds = new List<string>() };

        SetupMinimalInitialize(
            projects: new List<PipelineProject> { sourceProject, targetProject },
            templates: new List<PipelineJobTemplate> { MakeTemplate("t-1", "My Template"), MakeTemplate("t-2", "Other") });
        await _service.InitializeAsync();

        var savedProjects = new List<PipelineProject>();
        _mockConfigClient.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProjects.Add(p))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { sourceProject, targetProject });

        var (success, error, msg) = await _service.MoveTemplateToProjectAsync("t-1", "proj-src", "proj-tgt");

        Assert.True(success);
        Assert.Null(error);
        Assert.Contains("My Template", msg);
        Assert.Contains("Target", msg);

        var savedSource = savedProjects.First(p => p.Id == "proj-src");
        Assert.DoesNotContain("t-1", savedSource.TemplateIds);
        Assert.Contains("t-2", savedSource.TemplateIds);

        var savedTarget = savedProjects.First(p => p.Id == "proj-tgt");
        Assert.Contains("t-1", savedTarget.TemplateIds);
    }

    [Fact]
    public async Task MoveTemplateToProjectAsync_ReturnsSuccess_WhenSourceOrTargetProjectNotFound()
    {
        var (success, error, msg) = await _service.MoveTemplateToProjectAsync("t-1", "proj-src", "proj-tgt");

        Assert.True(success);
        Assert.Null(error);
        Assert.Null(msg);
        _mockConfigClient.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Loop Controls ──

    [Fact]
    public async Task StopLoopAsync_StopsAndPersistsConfig()
    {
        _mockConfigClient.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.StopLoopAsync();

        _mockConfigClient.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartLoopAsync_WhenLoopServiceThrows_ReturnsErrorTuple()
    {
        // PipelineLoopService internally tries to load config from its store deps (still old interfaces
        // for Task 6). Since no mock setup is provided, the mock throws by default.
        // This covers the "underlying loop service fails" path.
        var (success, error) = await _service.StartLoopAsync();

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task StartLoopAsync_WhenUpdateConfigThrows_ReturnsErrorTuple()
    {
        _mockConfigClient.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk full"));

        // Set up old store interfaces so PipelineLoopService can start the loop successfully.
        _mockLoopConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockLoopConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("ip-1") });
        _mockLoopConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { MakeProvider("rp-1", ProviderKind.Repository) });
        _mockLoopProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-1", Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
            });

        var (success, error) = await _service.StartLoopAsync();

        Assert.False(success);
        Assert.Contains("Disk full", error!);
    }

    // ── Cross-drawer coordination via coordinator ──

    [Fact]
    public async Task OpenIssueDrawerAsync_HidesPrAndEpicDrawers()
    {
        // TODO: [WARNING] PropagateProviderContext() is called before HideOtherDrawers inside
        // OpenIssueDrawerAsync, but it uses concrete type-casts (_mockIssueDrawerService is IssueDrawerService)
        // that silently no-op against Moq mocks. Provider context propagation is not tested here.
        // A regression in PropagateProviderContext (e.g., wrong provider list passed) would not be caught
        // by this test. Separate test coverage for PropagateProviderContext with concrete implementations
        // would be needed to validate the provider context reach.
        var template = MakeTemplate();
        _service.Templates.Add(template);

        _mockIssueDrawerService
            .Setup(s => s.OpenIssueDrawerAsync(It.IsAny<TemplateId>(), It.IsAny<IReadOnlyList<PipelineJobTemplate>>(), It.IsAny<Func<Task>?>()))
            .ReturnsAsync((string?)null);

        await _service.OpenIssueDrawerAsync("t-1");

        _mockPrReviewDrawerService.Verify(s => s.Hide(), Times.Once);
        _mockEpicDrawerService.Verify(s => s.Hide(), Times.Once);
        _mockIssueDrawerService.Verify(s => s.Hide(), Times.Never);
    }

    [Fact]
    public async Task OpenPrDrawerAsync_HidesIssueAndEpicDrawers()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);

        _mockPrReviewDrawerService
            .Setup(s => s.OpenPrDrawerAsync(It.IsAny<TemplateId>(), It.IsAny<IReadOnlyList<PipelineJobTemplate>>(), It.IsAny<Func<Task>?>()))
            .ReturnsAsync((string?)null);

        await _service.OpenPrDrawerAsync("t-1");

        _mockIssueDrawerService.Verify(s => s.Hide(), Times.Once);
        _mockEpicDrawerService.Verify(s => s.Hide(), Times.Once);
        _mockPrReviewDrawerService.Verify(s => s.Hide(), Times.Never);
    }

    [Fact]
    public async Task OpenEpicDrawerAsync_HidesIssueAndPrDrawers()
    {
        var template = MakeTemplate();
        _service.Templates.Add(template);

        _mockEpicDrawerService
            .Setup(s => s.OpenEpicDrawerAsync(It.IsAny<TemplateId>(), It.IsAny<IReadOnlyList<PipelineJobTemplate>>(), It.IsAny<IReadOnlyList<PipelineProject>>(), It.IsAny<Func<Task>?>()))
            .ReturnsAsync((string?)null);

        await _service.OpenEpicDrawerAsync("t-1");

        _mockIssueDrawerService.Verify(s => s.Hide(), Times.Once);
        _mockPrReviewDrawerService.Verify(s => s.Hide(), Times.Once);
        _mockEpicDrawerService.Verify(s => s.Hide(), Times.Never);
    }

    [Fact]
    public void ActiveDrawerTab_ReturnsIssue_WhenIssueDrawerOpen()
    {
        _issueDrawerState.IsOpen = true;
        Assert.Equal("issue", _service.ActiveDrawerTab);
    }

    [Fact]
    public void ActiveDrawerTab_ReturnsPr_WhenPrDrawerOpen()
    {
        _prDrawerState.IsOpen = true;
        Assert.Equal("pr", _service.ActiveDrawerTab);
    }

    [Fact]
    public void ActiveDrawerTab_ReturnsEpic_WhenEpicDrawerOpen()
    {
        _epicDrawerState.IsOpen = true;
        Assert.Equal("epic", _service.ActiveDrawerTab);
    }

    [Fact]
    public void ActiveDrawerTab_ReturnsEmpty_WhenNoDrawerOpen()
    {
        Assert.Equal("", _service.ActiveDrawerTab);
    }

    [Fact]
    public void CloseActiveDrawer_DelegatesToIssueDrawerService_WhenIssueDrawerOpen()
    {
        _issueDrawerState.IsOpen = true;

        _service.CloseActiveDrawer();

        _mockIssueDrawerService.Verify(s => s.CloseIssueDrawer(), Times.Once);
        _mockPrReviewDrawerService.Verify(s => s.ClosePrDrawer(), Times.Never);
        _mockEpicDrawerService.Verify(s => s.CloseEpicDrawer(), Times.Never);
    }

    [Fact]
    public void CloseActiveDrawer_DelegatesToPrDrawerService_WhenPrDrawerOpen()
    {
        _prDrawerState.IsOpen = true;

        _service.CloseActiveDrawer();

        _mockPrReviewDrawerService.Verify(s => s.ClosePrDrawer(), Times.Once);
        _mockIssueDrawerService.Verify(s => s.CloseIssueDrawer(), Times.Never);
    }

    [Fact]
    public void CloseActiveDrawer_DelegatesToEpicDrawerService_WhenEpicDrawerOpen()
    {
        _epicDrawerState.IsOpen = true;

        _service.CloseActiveDrawer();

        _mockEpicDrawerService.Verify(s => s.CloseEpicDrawer(), Times.Once);
        _mockIssueDrawerService.Verify(s => s.CloseIssueDrawer(), Times.Never);
    }

    // ── Forwarding wrappers ──

    [Fact]
    public async Task LoadDrawerIssuesAsync_DelegatesToIssueDrawerService()
    {
        var template = MakeTemplate();
        _mockIssueDrawerService.Setup(s => s.LoadDrawerIssuesAsync(template, 2))
            .ReturnsAsync((string?)null);

        var result = await _service.LoadDrawerIssuesAsync(template, 2);

        Assert.Null(result);
        _mockIssueDrawerService.Verify(s => s.LoadDrawerIssuesAsync(template, 2), Times.Once);
    }

    [Fact]
    public async Task LoadPrDrawerPageAsync_DelegatesToPrReviewDrawerService()
    {
        var template = MakeTemplate();
        _mockPrReviewDrawerService.Setup(s => s.LoadPrDrawerPageAsync(template, 1))
            .ReturnsAsync((string?)null);

        var result = await _service.LoadPrDrawerPageAsync(template, 1);

        Assert.Null(result);
        _mockPrReviewDrawerService.Verify(s => s.LoadPrDrawerPageAsync(template, 1), Times.Once);
    }

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_DelegatesToEpicDrawerService()
    {
        var template = MakeTemplate();
        _mockEpicDrawerService.Setup(s => s.LoadEpicDrawerIssuesAsync(template, 1))
            .ReturnsAsync((string?)null);

        var result = await _service.LoadEpicDrawerIssuesAsync(template, 1);

        Assert.Null(result);
        _mockEpicDrawerService.Verify(s => s.LoadEpicDrawerIssuesAsync(template, 1), Times.Once);
    }

    [Fact]
    public async Task IsIssueDistributedAsync_DelegatesToIssueDrawerService()
    {
        _mockIssueDrawerService.Setup(s => s.IsIssueDistributedAsync("42", "ip-1"))
            .ReturnsAsync(true);

        var result = await _service.IsIssueDistributedAsync("42", "ip-1");

        Assert.True(result);
        _mockIssueDrawerService.Verify(s => s.IsIssueDistributedAsync("42", "ip-1"), Times.Once);
    }

    [Fact]
    public async Task RefreshActiveIssuesAsync_DelegatesToIssueDrawerService()
    {
        _mockIssueDrawerService.Setup(s => s.RefreshActiveIssuesAsync())
            .Returns(Task.CompletedTask);

        await _service.RefreshActiveIssuesAsync();

        _mockIssueDrawerService.Verify(s => s.RefreshActiveIssuesAsync(), Times.Once);
    }

    [Fact]
    public void IsIssueActive_DelegatesToIssueDrawerService()
    {
        _mockIssueDrawerService.Setup(s => s.IsIssueActive((IssueIdentifier)"42", "ip-1"))
            .Returns(true);

        var result = _service.IsIssueActive("42", "ip-1");

        Assert.True(result);
    }

    // ── IssueDrawer CancellationToken (forwarded via DrawerState accessor) ──

    [Fact]
    public void IssueDrawer_CancellationToken_ReturnsNone_WhenNoDrawerOpen()
    {
        Assert.Equal(CancellationToken.None, _service.IssueDrawer.CancellationToken);
    }

    // ── Helper ──

    private void SetupMinimalInitialize(
        IReadOnlyList<PipelineProject>? projects = null,
        IReadOnlyList<PipelineJobTemplate>? templates = null)
    {
        _mockConfigClient.Setup(s => s.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockConfigClient.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigClient.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates ?? Array.Empty<PipelineJobTemplate>());
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects ?? Array.Empty<PipelineProject>());
        _mockConfigClient.Setup(s => s.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigClient.Setup(s => s.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockConfigClient.Setup(s => s.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
    }
}
