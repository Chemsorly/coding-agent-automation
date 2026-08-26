using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for IssueDrawerService in isolation: issue loading, dependency checking,
/// label filtering, pagination, dispatch, active issue tracking, and CancellationToken lifecycle.
/// Each test constructs IssueDrawerService with only its own dependencies (no IPipelineLoopService,
/// IProjectStore, or IConfigurationStore), verifying independent testability.
/// </summary>
public class IssueDrawerServiceTests
{
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IDependencyChecker> _mockDependencyChecker;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly IssueDrawerService _service;

    public IssueDrawerServiceTests()
    {
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockDependencyChecker = new Mock<IDependencyChecker>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();

        _service = new IssueDrawerService(
            _mockProviderFactory.Object,
            _mockDependencyChecker.Object,
            _mockWorkDistributor.Object,
            _mockDispatchOrchestration.Object);
    }

    private static ProviderConfig MakeProvider(string id, ProviderKind kind = ProviderKind.Issue) =>
        new() { Id = id, Kind = kind, ProviderType = "GitHub", DisplayName = "Test" };

    private static PipelineJobTemplate MakeTemplate(string id = "t-1") =>
        new() { Id = id, Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeIssue(string id = "42", string? description = null) =>
        new() { Identifier = id, Title = "Issue " + id, Labels = Array.Empty<string>(), Description = description };

    private static readonly IReadOnlyList<ProviderConfig> IssueProviders =
        new List<ProviderConfig> { new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" } };

    private static readonly IReadOnlyList<ProviderConfig> RepoProviders =
        new List<ProviderConfig> { new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" } };

    // ── Independent construction ──

    [Fact]
    public void CanBeConstructed_WithoutIPipelineLoopService_IProjectStore_IConfigurationStore()
    {
        // This test verifies the acceptance criterion: independently injectable without unrelated deps
        var svc = new IssueDrawerService(
            new Mock<IProviderFactory>().Object,
            new Mock<IDependencyChecker>().Object,
            new Mock<IWorkDistributor>().Object,
            new Mock<IDispatchOrchestrationService>().Object);
        Assert.NotNull(svc);
        svc.Dispose();
    }

    // ── LoadDrawerIssuesAsync ──

    [Fact]
    public async Task LoadDrawerIssuesAsync_SetsStateAndReturnsNull_OnSuccess()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        var mockProvider = SetupIssueProvider(items: new[] { MakeIssue("1") }, hasMore: true);

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Null(error);
        Assert.Single(_service.DrawerState.Items);
        Assert.True(_service.DrawerState.HasMore);
        Assert.Equal(1, _service.DrawerState.Page);
        Assert.False(_service.DrawerState.Loading);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_ReturnsError_WhenProviderNotFound()
    {
        _service.SetProviderContext(new List<ProviderConfig>(), RepoProviders);
        var template = MakeTemplate();

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Equal("Issue provider not found for this template.", error);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_AppliesLabelFilter_WhenSelectedLabelsPresent()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        _service.DrawerState.SelectedLabels.Add("bug");

        var mockProvider = new Mock<IIssueProvider>();
        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), HasMore = false, Page = 1, PageSize = 15 });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.NotNull(capturedLabels);
        Assert.Contains("bug", capturedLabels!);
    }

    // ── CheckDrawerDependenciesAsync ──

    [Fact]
    public async Task CheckDrawerDependenciesAsync_PopulatesReadiness_ForLoadedIssues()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        var mockProvider = SetupIssueProvider(items: new[]
        {
            new IssueSummary { Identifier = "10", Title = "A", Labels = Array.Empty<string>(), Description = "Blocked by #5" },
            new IssueSummary { Identifier = "11", Title = "B", Labels = Array.Empty<string>(), Description = "No deps" }
        });
        await _service.LoadDrawerIssuesAsync(template, 1);

        var blockedResult = new DependencyCheckResult { IsReady = false, BlockedBy = [5], TotalDependencies = 1 };
        _mockDependencyChecker.Setup(d => d.CheckAsync("10", "Blocked by #5", It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blockedResult);
        _mockDependencyChecker.Setup(d => d.CheckAsync("11", "No deps", It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        await _service.CheckDrawerDependenciesAsync(template);

        Assert.Equal(2, _service.DrawerReadiness.Count);
        Assert.False(_service.DrawerReadiness["10"].IsReady);
        Assert.True(_service.DrawerReadiness["11"].IsReady);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_ReturnsGracefully_WhenProviderNotFound()
    {
        _service.SetProviderContext(new List<ProviderConfig>(), RepoProviders);
        var template = MakeTemplate();

        await _service.CheckDrawerDependenciesAsync(template);

        Assert.Empty(_service.DrawerReadiness);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_InvokesOnProgress_PerIssue()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        SetupIssueProvider(items: new[] { MakeIssue("1"), MakeIssue("2") });
        await _service.LoadDrawerIssuesAsync(template, 1);

        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        int progressCount = 0;
        await _service.CheckDrawerDependenciesAsync(template, () => progressCount++);

        Assert.Equal(2, progressCount);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        _service.SetProviderContext(IssueProviders, RepoProviders);
        var template = MakeTemplate();
        SetupIssueProvider(items: new[] { MakeIssue("1") });
        await _service.LoadDrawerIssuesAsync(template, 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CheckDrawerDependenciesAsync(template, null, cts.Token));
    }

    // ── DispatchIssueAsync ──

    [Fact]
    public async Task DispatchIssueAsync_ReturnsError_WhenProvidersMissing()
    {
        var template = MakeTemplate();

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), template, new List<ProviderConfig>(), RepoProviders, null);

        Assert.False(success);
        Assert.Contains("no longer exist", error);
    }

    [Fact]
    public async Task DispatchIssueAsync_DbMode_DispatchedMessage_WhenNotQueued()
    {
        // TODO: [WARNING] This test does not verify that DistributeAndFinalizeAsync was called with the
        // specific request object from PrepareDistributionRequestAsync (vs. any other object). If
        // DispatchWithOrchestrationAsync is refactored to use a different request, the test still passes.
        // Strengthen by adding: _mockDispatchOrchestration.Verify(d => d.DistributeAndFinalizeAsync(request, ...), Times.Once).
        SetupDependencyCheckerReady();

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, _, msg) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.Contains("Dispatched", msg);
        Assert.DoesNotContain("Queued", msg);
    }

    [Fact]
    public async Task DispatchIssueAsync_DbMode_QueuedMessage_WhenQueued()
    {
        SetupDependencyCheckerReady();

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, true, null));

        var (success, _, msg) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.Contains("Queued", msg);
    }

    [Fact]
    public async Task DispatchIssueAsync_DbMode_DistributionFailed_ReturnsError()
    {
        SetupDependencyCheckerReady();

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(false, false, null));

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.False(success);
        Assert.Contains("distribution failed", error);
    }

    [Fact]
    public async Task DispatchIssueAsync_ReturnsError_WhenDependencyBlocked()
    {
        var mockProvider = new Mock<IIssueProvider>();
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);
        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult { IsReady = false, BlockedBy = [5], TotalDependencies = 1 });

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate(), IssueProviders, RepoProviders, null);

        Assert.False(success);
        Assert.Contains("blocked", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── DispatchFromIssueDrawerAsync ──

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_ReturnsError_WhenTemplateIsNull()
    {
        var (success, error, _) = await _service.DispatchFromIssueDrawerAsync(MakeIssue(), IssueProviders, RepoProviders, null);

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.DrawerState.IsDispatching);
    }

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_ClosesDrawer_OnSuccess()
    {
        // TODO: [WARNING] OpenIssueDrawerAsync internally calls RefreshActiveIssuesAsync →
        // _workDistributor.GetActiveIssueIdentifiersAsync. This mock is not set up here, so Moq returns
        // the default Task<HashSet<...>> (null). If GetActiveIssueIdentifiersAsync returns null,
        // ActiveIssues is assigned null, and a subsequent IsIssueActive call would throw
        // NullReferenceException. The test passes today only because no IsIssueActive call follows.
        // Fix: add _mockWorkDistributor.Setup(w => w.GetActiveIssueIdentifiersAsync(...)).ReturnsAsync(new HashSet<...>())
        // to this test (and any other test that calls OpenIssueDrawerAsync).
        SetupDependencyCheckerReady();

        var template = MakeTemplate();
        _service.SetProviderContext(IssueProviders, RepoProviders);
        SetupIssueProvider(items: new[] { MakeIssue() });
        await _service.OpenIssueDrawerAsync("t-1", new[] { template });
        Assert.True(_service.DrawerState.IsOpen);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration.Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var (success, _, _) = await _service.DispatchFromIssueDrawerAsync(MakeIssue(), IssueProviders, RepoProviders, null);

        Assert.True(success);
        Assert.False(_service.DrawerState.IsOpen);
    }

    // ── RefreshActiveIssuesAsync / IsIssueActive ──

    [Fact]
    public async Task RefreshActiveIssuesAsync_PopulatesActiveIssuesSet()
    {
        var expected = new HashSet<(IssueIdentifier, ProviderConfigId)> { ("42", "ip-1") };
        _mockWorkDistributor.Setup(w => w.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        await _service.RefreshActiveIssuesAsync();

        Assert.True(_service.IsIssueActive("42", "ip-1"));
        Assert.False(_service.IsIssueActive("99", "ip-1"));
    }

    // ── IsIssueDistributedAsync ──

    [Fact]
    public async Task IsIssueDistributedAsync_DelegatesToWorkDistributor()
    {
        _mockWorkDistributor.Setup(w => w.IsIssueDistributedAsync("42", "ip-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.IsIssueDistributedAsync("42", "ip-1");

        Assert.True(result);
        _mockWorkDistributor.Verify(w => w.IsIssueDistributedAsync("42", "ip-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CancellationToken lifecycle ──

    [Fact]
    public void IssueDrawer_CancellationToken_IsNotNone_BeforeDrawerOpen()
    {
        // CTS pre-initialized at construction — returns a valid (non-None) token
        // so public load methods work correctly before the first OpenAsync.
        Assert.NotEqual(CancellationToken.None, _service.DrawerState.CancellationToken);
        Assert.False(_service.DrawerState.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task IssueDrawer_CancellationToken_IsValid_WhenDrawerOpen()
    {
        var template = MakeTemplate();
        _service.SetProviderContext(IssueProviders, RepoProviders);
        SetupIssueProvider(items: new[] { MakeIssue() });

        await _service.OpenIssueDrawerAsync("t-1", new[] { template });

        var token = _service.DrawerState.CancellationToken;
        Assert.NotEqual(CancellationToken.None, token);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task IssueDrawer_CancellationToken_IsCancelled_AfterDrawerClose()
    {
        var template = MakeTemplate();
        _service.SetProviderContext(IssueProviders, RepoProviders);
        SetupIssueProvider(items: new[] { MakeIssue() });

        await _service.OpenIssueDrawerAsync("t-1", new[] { template });
        var token = _service.DrawerState.CancellationToken;

        _service.CloseIssueDrawer();

        Assert.True(token.IsCancellationRequested);
    }

    // ── Dispose ──

    [Fact]
    public async Task Dispose_CancelsCts()
    {
        var template = MakeTemplate();
        _service.SetProviderContext(IssueProviders, RepoProviders);
        SetupIssueProvider(items: new[] { MakeIssue() });

        await _service.OpenIssueDrawerAsync("t-1", new[] { template });
        var token = _service.DrawerState.CancellationToken;
        Assert.False(token.IsCancellationRequested);

        _service.Dispose();

        Assert.True(token.IsCancellationRequested);

        // double-dispose safe
        _service.Dispose();
    }

    // ── Helpers ──

    private Mock<IIssueProvider> SetupIssueProvider(
        IEnumerable<IssueSummary>? items = null,
        bool hasMore = false)
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = (items ?? Enumerable.Empty<IssueSummary>()).ToList(),
                HasMore = hasMore, Page = 1, PageSize = 15
            });
        mockProvider.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);
        return mockProvider;
    }

    private void SetupDependencyCheckerReady()
    {
        var mockProvider = new Mock<IIssueProvider>();
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);
        _mockDependencyChecker.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);
    }

    private static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "42",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "manual",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600,
        ProviderConfigs = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
        }
    };
}
