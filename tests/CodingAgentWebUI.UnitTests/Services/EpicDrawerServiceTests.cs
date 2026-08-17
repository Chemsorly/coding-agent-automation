using Moq;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for EpicDrawerService in isolation: epic loading with deduplication,
/// label filtering, dispatch phase selection, close-on-success behavior.
/// Each test constructs EpicDrawerService with only its own dependencies.
/// </summary>
public class EpicDrawerServiceTests
{
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;
    private readonly Mock<IAgentRegistryService> _mockAgentRegistry;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly EpicDrawerService _service;

    private static readonly IReadOnlyList<ProviderConfig> IssueProviders =
        new List<ProviderConfig> { new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" } };

    private static readonly IReadOnlyList<ProviderConfig> RepoProviders =
        new List<ProviderConfig> { new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" } };

    public EpicDrawerServiceTests()
    {
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockAgentRegistry = new Mock<IAgentRegistryService>();
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();

        _service = new EpicDrawerService(
            _mockProviderFactory.Object,
            _mockWorkDistributor.Object,
            _mockAgentRegistry.Object,
            _mockDispatchOrchestration.Object);
    }

    private static PipelineJobTemplate MakeTemplate(string id = "t-1") =>
        new() { Id = id, Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeEpic(string id, params string[] labels) =>
        new() { Identifier = id, Title = "Epic " + id, Labels = labels };

    // ── Independent construction ──

    [Fact]
    public void CanBeConstructed_WithoutIPipelineLoopService_IProjectStore_IConfigurationStore()
    {
        var svc = new EpicDrawerService(
            new Mock<IProviderFactory>().Object,
            new Mock<IWorkDistributor>().Object,
            new Mock<IAgentRegistryService>().Object,
            new Mock<IDispatchOrchestrationService>().Object);
        Assert.NotNull(svc);
        svc.Dispose();
    }

    // ── LoadEpicDrawerIssuesAsync — deduplication regression ──

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_DeduplicatesIssuesWithBothEpicLabels()
    {
        // Regression: issues with both agent:epic AND agent:epic-approved appear in both API queries.
        // The result must be deduplicated by Identifier.
        _service.SetProviderContext(IssueProviders);
        var template = MakeTemplate();

        var duplicateIssue = MakeEpic("100", "agent:epic", "agent:epic-approved");
        var epicOnlyIssue = MakeEpic("101", "agent:epic");
        var approvedOnlyIssue = MakeEpic("102", "agent:epic-approved");

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(1, 8,
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains("agent:epic") && !l.Contains("agent:epic-approved")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary> { duplicateIssue, epicOnlyIssue }, HasMore = false, Page = 1, PageSize = 8 });
        mockProvider.Setup(p => p.ListOpenIssuesAsync(1, 8,
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains("agent:epic-approved")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary> { duplicateIssue, approvedOnlyIssue }, HasMore = false, Page = 1, PageSize = 8 });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        var error = await _service.LoadEpicDrawerIssuesAsync(template, 1);

        Assert.Null(error);
        // 3 unique issues, NOT 4 (duplicate counted once)
        Assert.Equal(3, _service.DrawerState.Items.Count);
        Assert.Equal(1, _service.DrawerState.Items.Count(i => i.Identifier == "100"));
    }

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_ReturnsError_WhenProviderNotFound()
    {
        _service.SetProviderContext(new List<ProviderConfig>());
        var template = MakeTemplate();

        var error = await _service.LoadEpicDrawerIssuesAsync(template, 1);

        Assert.Equal("Epic issue provider not found.", error);
    }

    // ── LoadEpicDrawerLabelsAsync — EpicIssueProviderId from project ──

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_UsesEpicIssueProviderId_WhenProjectHasOne()
    {
        // TODO: [WARNING] Both the epic-specific provider mock and the fallback provider mock return the
        // same mockProvider.Object instance. The assertion Assert.Equal(epicProviderId, usedProvider!.Id)
        // captures the Callback only if the epic-specific CreateIssueProvider overload is hit — but if
        // the logic fell through to the default template.IssueProviderId, the fallback mock (p.Id != epicProviderId)
        // returns mockProvider.Object without invoking the Callback, so usedProvider stays null and the
        // Assert.NotNull would catch it. However, both providers would produce the same test outcome for
        // load behavior. Use a distinct mock object for the fallback to make provider-selection failures
        // observable via behavior divergence, not just usedProvider identity.
        var epicProviderId = "epic-ip-2";
        var epicProviders = new List<ProviderConfig>
        {
            IssueProviders[0],
            new() { Id = epicProviderId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Epic Provider" }
        };
        _service.SetProviderContext(epicProviders);

        var project = new PipelineProject { Id = "p-1", Name = "P", TemplateIds = new[] { "t-1" }, EpicIssueProviderId = epicProviderId };
        var projects = new List<PipelineProject> { project };
        var template = MakeTemplate();

        ProviderConfig? usedProvider = null;
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), HasMore = false, Page = 1, PageSize = 8 });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(p => p.Id == epicProviderId)))
            .Callback<ProviderConfig>(p => usedProvider = p)
            .Returns(mockProvider.Object);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(p => p.Id != epicProviderId)))
            .Returns(mockProvider.Object);

        // Open via the projects-aware method to set the project context
        await _service.OpenEpicDrawerAsync("t-1", new[] { template }, projects);

        // Verify the epic-specific provider was used
        Assert.NotNull(usedProvider);
        Assert.Equal(epicProviderId, usedProvider!.Id);
    }

    // ── DispatchDecompositionAsync — phase type selection ──

    [Fact]
    public async Task DispatchDecompositionAsync_UsesDecompositionAnalysis_ForRegularEpic()
    {
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(false);

        DecompositionDispatchOrchestrationRequest? capturedRequest = null;
        _mockDispatchOrchestration.Setup(d => d.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecompositionDispatchOrchestrationRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(CreateMinimalRequest(WorkItemTaskType.Decomposition));
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var issue = MakeEpic("e-1", "agent:epic"); // NOT agent:epic-approved
        var (success, _, _) = await _service.DispatchDecompositionAsync(issue, MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.NotNull(capturedRequest);
        Assert.Equal(PipelineRunType.DecompositionAnalysis, capturedRequest!.PhaseType);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_UsesDecomposition_ForApprovedEpic()
    {
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(false);

        DecompositionDispatchOrchestrationRequest? capturedRequest = null;
        _mockDispatchOrchestration.Setup(d => d.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecompositionDispatchOrchestrationRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(CreateMinimalRequest(WorkItemTaskType.Decomposition));
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var issue = MakeEpic("e-1", "agent:epic-approved"); // approved
        var (success, _, _) = await _service.DispatchDecompositionAsync(issue, MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.NotNull(capturedRequest);
        Assert.Equal(PipelineRunType.Decomposition, capturedRequest!.PhaseType);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_ReturnsError_WhenProvidersMissing()
    {
        var (success, error, _) = await _service.DispatchDecompositionAsync(
            MakeEpic("e-1"), MakeTemplate(), new List<ProviderConfig>(), RepoProviders, null);

        Assert.False(success);
        Assert.Contains("no longer exist", error);
    }

    // ── DispatchFromEpicDrawerAsync ──

    [Fact]
    public async Task DispatchFromEpicDrawerAsync_ClosesDrawer_OnSuccess()
    {
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(false);
        _service.SetProviderContext(IssueProviders);

        var template = MakeTemplate();
        SetupIssueProviderForEpic(items: new[] { MakeEpic("e-1", "agent:epic") });
        await _service.OpenEpicDrawerAsync("t-1", new[] { template }, new List<PipelineProject>());
        Assert.True(_service.DrawerState.IsOpen);

        _mockDispatchOrchestration.Setup(d => d.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMinimalRequest(WorkItemTaskType.Decomposition));
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, _, _) = await _service.DispatchFromEpicDrawerAsync(MakeEpic("e-1"), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.False(_service.DrawerState.IsOpen);
    }

    [Fact]
    public async Task DispatchFromEpicDrawerAsync_ReturnsError_WhenTemplateIsNull()
    {
        var (success, error, _) = await _service.DispatchFromEpicDrawerAsync(MakeEpic("e-1"), IssueProviders, RepoProviders, null);

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.DrawerState.IsDispatching);
    }

    // ── Hide / CloseEpicDrawer ──

    [Fact]
    public void Hide_SetsIsOpenToFalse()
    {
        _service.DrawerState.IsOpen = true;
        _service.Hide();
        Assert.False(_service.DrawerState.IsOpen);
    }

    // ── Helpers ──

    private void SetupIssueProviderForEpic(IEnumerable<IssueSummary>? items = null)
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = (items ?? Enumerable.Empty<IssueSummary>()).ToList(),
                HasMore = false, Page = 1, PageSize = 8
            });
        mockProvider.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);
    }

    private static JobDistributionRequest CreateMinimalRequest(WorkItemTaskType taskType = WorkItemTaskType.Decomposition) => new()
    {
        IssueIdentifier = "e-1",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "manual",
        TaskType = taskType,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600,
        ProviderConfigs = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
        }
    };
}
