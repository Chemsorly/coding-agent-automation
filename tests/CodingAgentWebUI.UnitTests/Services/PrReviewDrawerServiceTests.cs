using Moq;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for PrReviewDrawerService in isolation: PR loading, label filtering,
/// pagination, dispatch, and close-on-success behavior.
/// Each test constructs PrReviewDrawerService with only its own dependencies.
/// </summary>
public class PrReviewDrawerServiceTests
{
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;
    private readonly Mock<IAgentRegistryService> _mockAgentRegistry;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly PrReviewDrawerService _service;

    private static readonly IReadOnlyList<ProviderConfig> IssueProviders =
        new List<ProviderConfig> { new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" } };

    private static readonly IReadOnlyList<ProviderConfig> RepoProviders =
        new List<ProviderConfig> { new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" } };

    public PrReviewDrawerServiceTests()
    {
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockAgentRegistry = new Mock<IAgentRegistryService>();
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();

        _service = new PrReviewDrawerService(
            _mockProviderFactory.Object,
            _mockWorkDistributor.Object,
            _mockAgentRegistry.Object,
            _mockDispatchOrchestration.Object);
    }

    private static PipelineJobTemplate MakeTemplate(string id = "t-1") =>
        new() { Id = id, Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static PullRequestSummary MakePr(string id = "5") =>
        new() { Identifier = id, Number = int.Parse(id), Title = "PR " + id, BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Description = "", Labels = Array.Empty<string>(), IsDraft = false };

    // ── Independent construction ──

    [Fact]
    public void CanBeConstructed_WithoutIPipelineLoopService_IProjectStore_IConfigurationStore()
    {
        var svc = new PrReviewDrawerService(
            new Mock<IProviderFactory>().Object,
            new Mock<IWorkDistributor>().Object,
            new Mock<IAgentRegistryService>().Object);
        Assert.NotNull(svc);
        svc.Dispose();
    }

    // ── LoadPrDrawerPageAsync ──

    [Fact]
    public async Task LoadPrDrawerPageAsync_LoadsData_WhenRepoProviderExists()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        SetupRepoProvider(items: new[] { MakePr("1"), MakePr("2") }, hasMore: true);

        var error = await _service.LoadPrDrawerPageAsync(template, 1);

        Assert.Null(error);
        Assert.Equal(2, _service.DrawerState.Items.Count);
        Assert.True(_service.DrawerState.HasMore);
        Assert.Equal(1, _service.DrawerState.Page);
    }

    [Fact]
    public async Task LoadPrDrawerPageAsync_ReturnsNullAndEmptyList_WhenRepoProviderNotFound()
    {
        _service.SetProviderContext(IssueProviders, new List<ProviderConfig>());
        var template = MakeTemplate();

        var error = await _service.LoadPrDrawerPageAsync(template, 1);

        Assert.Null(error);
        Assert.Empty(_service.DrawerState.Items);
    }

    // ── ClearPrDrawerLabelFilter ── (also covers pre-existing bug fix: must clear Items)

    [Fact]
    public async Task ClearPrDrawerLabelFilter_ClearsItemsAndPage()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        SetupRepoProvider(items: new[] { MakePr("10") }, hasMore: true);
        await _service.LoadPrDrawerPageAsync(template, 2);
        Assert.Single(_service.DrawerState.Items);
        Assert.True(_service.DrawerState.HasMore);
        Assert.Equal(2, _service.DrawerState.Page);

        _service.ClearPrDrawerLabelFilter();

        Assert.Empty(_service.DrawerState.Items);
        Assert.Equal(1, _service.DrawerState.Page);
        Assert.False(_service.DrawerState.HasMore);
    }

    // ── DispatchPrReviewAsync ──

    [Fact]
    public async Task DispatchPrReviewAsync_ReturnsError_WhenRequiresConnectedAgentsAndNoneConnected()
    {
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(true);
        _mockAgentRegistry.Setup(r => r.GetAllAgents()).Returns(new List<AgentEntry>());

        var (success, error, _) = await _service.DispatchPrReviewAsync(MakePr(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.False(success);
        Assert.Contains("no agents are currently connected", error);
    }

    [Fact]
    public async Task DispatchPrReviewAsync_DbMode_ReturnsSuccess_WhenOrchestrationSucceeds()
    {
        // TODO: [WARNING] This test asserts only Assert.True(success) and does not check the returned
        // msg string. Swapping the queuedMessage and dispatchedMessage arguments in
        // DispatchWithOrchestrationAsync would not be caught. Add: var (success, _, msg) = ...
        // and Assert.DoesNotContain("Queued", msg) to complement the queued-message test.
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(false);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareReviewDistributionRequestAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, _, _) = await _service.DispatchPrReviewAsync(MakePr(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        _mockDispatchOrchestration.Verify(d => d.PrepareReviewDistributionRequestAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchPrReviewAsync_DbMode_QueuedMessage_WhenQueued()
    {
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(false);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareReviewDistributionRequestAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, true, null));

        var (success, _, msg) = await _service.DispatchPrReviewAsync(MakePr(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.Contains("Queued", msg);
    }

    // ── DispatchFromPrDrawerAsync ──

    [Fact]
    public async Task DispatchFromPrDrawerAsync_ReturnsError_WhenTemplateIsNull()
    {
        var (success, error, _) = await _service.DispatchFromPrDrawerAsync(MakePr(), IssueProviders, RepoProviders, null);

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.DrawerState.IsDispatching);
    }

    [Fact]
    public async Task DispatchFromPrDrawerAsync_DoesNotCloseDrawer_OnSuccess()
    {
        // PR drawer stays open after dispatch (unlike issue/epic)
        _mockWorkDistributor.Setup(w => w.RequiresConnectedAgents).Returns(false);
        _service.SetProviderContext(IssueProviders, RepoProviders);

        var template = MakeTemplate();
        SetupRepoProvider(items: new[] { MakePr() });
        await _service.OpenPrDrawerAsync("t-1", new[] { template });
        Assert.True(_service.DrawerState.IsOpen);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareReviewDistributionRequestAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, _, _) = await _service.DispatchFromPrDrawerAsync(MakePr(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.True(_service.DrawerState.IsOpen); // PR drawer stays open
    }

    // ── Hide / ClosePrDrawer ──

    [Fact]
    public async Task ClosePrDrawer_NullsTemplate()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        SetupRepoProvider(items: new[] { MakePr() });
        await _service.OpenPrDrawerAsync("t-1", new[] { MakeTemplate() });
        Assert.NotNull(_service.DrawerState.Template);

        _service.ClosePrDrawer();

        Assert.Null(_service.DrawerState.Template);
    }

    [Fact]
    public void Hide_SetsIsOpenToFalse()
    {
        _service.DrawerState.IsOpen = true;
        _service.Hide();
        Assert.False(_service.DrawerState.IsOpen);
    }

    // ── Helpers ──

    private void SetupRepoProvider(IEnumerable<PullRequestSummary>? items = null, bool hasMore = false)
    {
        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.ListOpenPullRequestsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = (items ?? Enumerable.Empty<PullRequestSummary>()).ToList(),
                HasMore = hasMore, Page = 1, PageSize = 15
            });
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepo.Object);

        var mockIssue = new Mock<IIssueProvider>();
        mockIssue.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockIssue.Object);
    }

    private static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "5",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "manual",
        TaskType = WorkItemTaskType.Review,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600,
        ProviderConfigs = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
        }
    };
}
