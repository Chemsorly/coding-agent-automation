using Moq;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for IssueDrawerService in isolation: issue loading, dependency checking,
/// label filtering, pagination, dispatch, active issue tracking, and CancellationToken lifecycle.
/// Each test constructs IssueDrawerService with only its own dependencies (no IPipelineLoopService,
/// IProjectStore, or IConfigurationStore), verifying independent testability. Since spec 051 2.3b the
/// API browses the providers (<see cref="IUiBrowseClient"/>); its TemplateBrowseServiceTests cover that.
/// </summary>
public class IssueDrawerServiceTests
{
    private static readonly string[] BugAndDocsLabels = ["bug", "docs"];

    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IDependencyChecker> _mockDependencyChecker;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly Mock<IUiBrowseClient> _mockBrowse;
    private readonly IssueDrawerService _service;

    public IssueDrawerServiceTests()
    {
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockDependencyChecker = new Mock<IDependencyChecker>();
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();
        _mockBrowse = new Mock<IUiBrowseClient>();

        // Default stubs so opening the drawer does not throw in tests that do not care about it.
        _mockBrowse.Setup(b => b.GetActiveIssuesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UiActiveIssue>());
        _mockBrowse.Setup(b => b.GetLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockBrowse.Setup(b => b.GetIssueReadinessAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DependencyCheckResult>());

        _service = new IssueDrawerService(
            _mockProviderFactory.Object,
            _mockDependencyChecker.Object,
            _mockWorkDistributor.Object,
            _mockDispatchOrchestration.Object,
            _mockBrowse.Object);
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
            new Mock<IDispatchOrchestrationService>().Object,
            new Mock<IUiBrowseClient>().Object);
        Assert.NotNull(svc);
        svc.Dispose();
    }

    // ── LoadDrawerIssuesAsync ──

    [Fact]
    public async Task LoadDrawerIssuesAsync_SetsStateAndReturnsNull_OnSuccess()
    {
        var template = MakeTemplate();
        SetupIssueProvider(items: new[] { MakeIssue("1") }, hasMore: true);

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Null(error);
        Assert.Single(_service.DrawerState.Items);
        Assert.True(_service.DrawerState.HasMore);
        Assert.Equal(1, _service.DrawerState.Page);
        Assert.False(_service.DrawerState.Loading);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_ReturnsTheApisReason_WhenItRefuses()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetIssuesAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Issue provider not found for this template."));

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Equal("Issue provider not found for this template.", error);
        Assert.Empty(_service.DrawerState.Items);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_WhenTheApiCannotBeReached_ReturnsAFailureMessage()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetIssuesAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var error = await _service.LoadDrawerIssuesAsync(template, 1);

        Assert.Equal("Failed to load issues: connection refused", error);
    }

    [Fact]
    public async Task LoadDrawerIssuesAsync_AppliesLabelFilter_WhenSelectedLabelsPresent()
    {
        var template = MakeTemplate();
        _service.DrawerState.SelectedLabels.Add("bug");
        IReadOnlyList<string>? capturedLabels = null;
        _mockBrowse.Setup(b => b.GetIssuesAsync("t-1", 2, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), HasMore = false, Page = 2, PageSize = 15 });

        await _service.LoadDrawerIssuesAsync(template, 2);

        Assert.NotNull(capturedLabels);
        Assert.Contains("bug", capturedLabels!);
    }

    [Fact]
    public async Task LoadDrawerLabelsAsync_LoadsTheTrackersLabels_AndClearsThemWhenTheApiFails()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetLabelsAsync("t-1", It.IsAny<CancellationToken>())).ReturnsAsync(BugAndDocsLabels);

        await _service.LoadDrawerLabelsAsync(template);
        Assert.Equal(BugAndDocsLabels, _service.DrawerState.Labels);

        _mockBrowse.Setup(b => b.GetLabelsAsync("t-1", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Failed to load labels: boom"));
        await _service.LoadDrawerLabelsAsync(template);
        Assert.Empty(_service.DrawerState.Labels);
    }

    // ── CheckDrawerDependenciesAsync ──

    [Fact]
    public async Task CheckDrawerDependenciesAsync_PopulatesReadiness_ForThePage()
    {
        var template = MakeTemplate();
        SetupIssueProvider(items: new[] { MakeIssue("10"), MakeIssue("11") });
        await _service.LoadDrawerIssuesAsync(template, 1);
        var blockedResult = new DependencyCheckResult { IsReady = false, BlockedBy = [5], TotalDependencies = 1 };
        _mockBrowse.Setup(b => b.GetIssueReadinessAsync("t-1", 1, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DependencyCheckResult> { ["10"] = blockedResult, ["11"] = DependencyCheckResult.NoDependencies });

        await _service.CheckDrawerDependenciesAsync(template, null, CancellationToken.None);

        Assert.Equal(2, _service.DrawerReadiness.Count);
        Assert.False(_service.DrawerReadiness["10"].IsReady);
        Assert.True(_service.DrawerReadiness["11"].IsReady);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_ReturnsGracefully_WhenTheApiRefuses()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetIssueReadinessAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Issue provider not found for this template."));

        await _service.CheckDrawerDependenciesAsync(template, null, CancellationToken.None);

        Assert.Empty(_service.DrawerReadiness);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_InvokesOnProgress_OnceTheResultsAreIn()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetIssueReadinessAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DependencyCheckResult> { ["1"] = DependencyCheckResult.NoDependencies, ["2"] = DependencyCheckResult.NoDependencies });

        int progressCount = 0;
        await _service.CheckDrawerDependenciesAsync(template, () => progressCount++, CancellationToken.None);

        Assert.Equal(1, progressCount);
    }

    [Fact]
    public async Task CheckDrawerDependenciesAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        var template = MakeTemplate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockBrowse.Setup(b => b.GetIssueReadinessAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

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
        // OpenIssueDrawerAsync internally calls RefreshActiveIssuesAsync →
        // _apiClient.GetActiveIdentifiersAsync and GetPendingAsync. These are already stubbed
        // in the constructor to return empty results, so no additional setup is needed here.
        SetupDependencyCheckerReady();

        var template = MakeTemplate();
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

    // ── RefreshActiveIssuesAsync / IsIssueActive / GetIssueWorkItemStatus ──

    private void SetupActiveIssues(params UiActiveIssue[] active) =>
        _mockBrowse.Setup(b => b.GetActiveIssuesAsync("t-1", It.IsAny<CancellationToken>())).ReturnsAsync(active);

    [Fact]
    public async Task RefreshActiveIssuesAsync_PopulatesActiveIssuesSet()
    {
        SetupActiveIssues(new UiActiveIssue("42", "ip-1", WorkItemStatus.Pending));

        await _service.RefreshActiveIssuesAsync("t-1");

        Assert.True(_service.IsIssueActive("42", "ip-1"));
        Assert.False(_service.IsIssueActive("99", "ip-1"));
        Assert.False(_service.IsIssueActive("42", "other-provider"));
    }

    // ── IsIssueDistributedAsync ──

    [Fact]
    public async Task IsIssueDistributedAsync_DelegatesToWorkDistributor()
    {
        _mockWorkDistributor.Setup(w => w.IsIssueDistributedAsync((IssueIdentifier)"42", (ProviderConfigId)"ip-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.IsIssueDistributedAsync((IssueIdentifier)"42", (ProviderConfigId)"ip-1");

        Assert.True(result);
        _mockWorkDistributor.Verify(w => w.IsIssueDistributedAsync((IssueIdentifier)"42", (ProviderConfigId)"ip-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── GetIssueWorkItemStatus ──

    [Theory]
    [InlineData(WorkItemStatus.Pending)]
    [InlineData(WorkItemStatus.Running)]
    public async Task GetIssueWorkItemStatus_ReturnsTheApisBadge(WorkItemStatus badge)
    {
        SetupActiveIssues(new UiActiveIssue("42", "ip-1", badge));

        await _service.RefreshActiveIssuesAsync("t-1");

        Assert.Equal(badge, _service.GetIssueWorkItemStatus("42", "ip-1"));
    }

    [Fact]
    public async Task GetIssueWorkItemStatus_WhenIssueNotInQueue_ReturnsNull()
    {
        SetupActiveIssues();

        await _service.RefreshActiveIssuesAsync("t-1");

        Assert.Null(_service.GetIssueWorkItemStatus("99", "ip-1"));
    }

    [Fact]
    public async Task OpenIssueDrawerAsync_RefreshesTheTemplatesBadges()
    {
        SetupIssueProvider(items: new[] { MakeIssue() });
        SetupActiveIssues(new UiActiveIssue("42", "ip-1", WorkItemStatus.Running));

        await _service.OpenIssueDrawerAsync("t-1", new[] { MakeTemplate() });

        Assert.Equal(WorkItemStatus.Running, _service.GetIssueWorkItemStatus("42", "ip-1"));
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

    /// <summary>Makes the API return <paramref name="items"/> as the template's first issue page.</summary>
    private void SetupIssueProvider(
        IEnumerable<IssueSummary>? items = null,
        bool hasMore = false)
    {
        _mockBrowse.Setup(b => b.GetIssuesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = (items ?? Enumerable.Empty<IssueSummary>()).ToList(),
                HasMore = hasMore,
                Page = 1,
                PageSize = 15
            });
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

    // ── Label clear+set before dispatch (manual force-requeue) ──

    /// <summary>
    /// Manual dispatch on an issue with a blocking label (agent:cancelled) must:
    ///   1. Call AddLabelAsync("agent:next") on the issue provider before creating the WorkItem.
    ///   2. Call RemoveLabelAsync("agent:cancelled") to clear the blocking label.
    ///   3. Proceed with orchestration dispatch and succeed.
    /// This verifies the fix for the self-reinforcing cancellation loop: without the label clear,
    /// DispatchLoop would see agent:cancelled, cancel the WorkItem, and stamp agent:cancelled again.
    /// Note: agent:epic-review is intentionally excluded — it is blocked entirely, not cleared+requeued.
    /// </summary>
    [Theory]
    [InlineData(AgentLabels.Cancelled)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.WontDo)]
    [InlineData(AgentLabels.Done)]
    public async Task DispatchIssueAsync_WithBlockingLabel_ClearsLabelAndSetsNext_BeforeDispatch(string blockingLabel)
    {
        // Arrange — issue has a blocking label
        var issue = new IssueSummary
        {
            Identifier = "42",
            Title = "Issue 42",
            Labels = new[] { blockingLabel }
        };
        var template = MakeTemplate();

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, true, null));


        // Act
        var (success, error, _) = await _service.DispatchIssueAsync(issue, template, IssueProviders, RepoProviders, null);

        // Assert — dispatch succeeds
        Assert.True(success, $"Expected success but got error: {error}");

        // agent:next must have been added to the issue before orchestration
        mockProvider.Verify(p => p.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next,
            It.IsAny<CancellationToken>()),
            Times.Once,
            "agent:next must be set on the issue before WorkItem creation");

        // The blocking label must have been removed
        mockProvider.Verify(p => p.RemoveLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            blockingLabel,
            It.IsAny<CancellationToken>()),
            Times.Once,
            $"{blockingLabel} must be removed from the issue before WorkItem creation");
    }

    /// <summary>
    /// Manual dispatch of an issue carrying agent:epic-review must be rejected with a clean
    /// error before any label manipulation or WorkItem creation occurs. agent:epic-review marks
    /// an issue awaiting human approval of an epic plan; re-dispatching it as an implementation
    /// job would bypass the approval gate. The UI dispatch button should be disabled for such
    /// issues, but even if the API is called directly the response must be a clear failure.
    /// </summary>
    [Fact]
    public async Task DispatchIssueAsync_WithEpicReviewLabel_ReturnsError_BeforeDispatch()
    {
        // Arrange — issue is in the epic-review state
        var issue = new IssueSummary
        {
            Identifier = "99",
            Title = "Epic issue awaiting approval",
            Labels = new[] { AgentLabels.EpicReview }
        };
        var template = MakeTemplate();

        var mockProvider = new Mock<IIssueProvider>();
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        // TODO: This mock returns NoDependencies so execution reaches the EpicReview guard.
        // However, the test does not assert on *ordering* between the dependency check and the
        // EpicReview guard. If the guards were ever swapped (EpicReview check moved before the
        // dependency check), this test would still pass trivially. Consider adding a companion
        // test where the issue has blocked dependencies AND agent:epic-review, asserting that
        // the EpicReview rejection message is returned — not the "blocked by dependencies" message
        // — to verify the EpicReview guard fires first.
        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);


        // Act
        var (success, error, _) = await _service.DispatchIssueAsync(issue, template, IssueProviders, RepoProviders, null);

        // Assert — dispatch is rejected with a descriptive error
        Assert.False(success, "Dispatch must be rejected for agent:epic-review issues");
        Assert.NotNull(error);
        // TODO: This assertion only verifies the label name appears in the error string. It would
        // pass even if the message were changed to something unhelpful (e.g., "EpicReview blocked").
        // The acceptance criterion requires a "clean error", making the message content observable
        // behavior. Consider strengthening to Assert.Contains("human approval", error, ...) or
        // Assert.Contains("epic approval workflow", error, ...) to pin the actionable content.
        Assert.Contains(AgentLabels.EpicReview, error, StringComparison.OrdinalIgnoreCase);

        // No label mutations must have occurred
        mockProvider.Verify(p => p.AddLabelAsync(
            It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Label management must not fire for epic-review issues — dispatch is blocked before the label-clear path");
        mockProvider.Verify(p => p.RemoveLabelAsync(
            It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Label management must not fire for epic-review issues — dispatch is blocked before the label-clear path");

        // Orchestration must not have been invoked
        _mockDispatchOrchestration.Verify(d => d.PrepareDistributionRequestAsync(
            It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Orchestration must not be reached for epic-review issues");
    }

    /// <summary>
    /// Manual dispatch on a clean issue (no blocking label) must NOT call AddLabelAsync or
    /// RemoveLabelAsync for label management — orchestration proceeds without touching labels.
    /// The label swap to agent:in-progress happens later in the DispatchLoop, not here.
    /// </summary>
    [Fact]
    public async Task DispatchIssueAsync_WithNoBlockingLabel_DoesNotTouchLabels()
    {
        // Arrange — issue has agent:next (clean, scheduler-ready state)
        var issue = MakeIssue("42");
        var template = MakeTemplate();

        var mockProvider = new Mock<IIssueProvider>();
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, true, null));


        // Act — issue has no labels (MakeIssue returns Labels = Array.Empty<string>())
        var (success, _, _) = await _service.DispatchIssueAsync(issue, template, IssueProviders, RepoProviders, null);

        // Assert — no label mutations for clean issues
        Assert.True(success);
        mockProvider.Verify(p => p.AddLabelAsync(
            It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Label management must not fire for issues without blocking labels");
        mockProvider.Verify(p => p.RemoveLabelAsync(
            It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Label management must not fire for issues without blocking labels");
    }

    /// <summary>
    /// If label-clear succeeds (agent:next is set on GitHub) but DistributeAndFinalizeAsync then
    /// fails (e.g. orchestration timeout, 409 from the unique index), DispatchIssueAsync must
    /// return an error tuple. The issue is left with agent:next and no WorkItem — the scheduler
    /// loop will pick it up naturally on its next cycle, so the state is recoverable.
    /// </summary>
    [Fact]
    public async Task DispatchIssueAsync_WithBlockingLabel_LabelClearSucceeds_OrchestrationFails_ReturnsError()
    {
        // Arrange — issue carries agent:cancelled, label-clear succeeds, orchestration fails
        var issue = new IssueSummary { Identifier = "42", Title = "Issue 42", Labels = new[] { AgentLabels.Cancelled } };
        var template = MakeTemplate();

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var request = CreateMinimalRequest();
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(false, false, "unique index violation"));   // ← orchestration fails


        // Act
        var (success, error, _) = await _service.DispatchIssueAsync(issue, template, IssueProviders, RepoProviders, null);

        // Assert — error returned to caller
        Assert.False(success);
        Assert.Contains("distribution failed", error, StringComparison.OrdinalIgnoreCase);

        // Label-clear still fired — issue is left with agent:next on GitHub (recoverable by scheduler)
        mockProvider.Verify(p => p.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"), AgentLabels.Next, It.IsAny<CancellationToken>()),
            Times.Once, "agent:next must still be set even if orchestration subsequently fails");
    }

    /// <summary>
    /// If CreateIssueProvider throws during the label-clear block (e.g. bad provider config,
    /// factory misconfiguration), DispatchIssueAsync must catch the exception and return an error
    /// tuple rather than letting it bubble up to the Blazor circuit handler uncaught.
    /// </summary>
    [Fact]
    public async Task DispatchIssueAsync_WithBlockingLabel_CreateIssueProviderThrows_ReturnsErrorTuple()
    {
        // Arrange — issue has agent:error
        // The factory is called twice: once for the dependency check, once for the label-clear.
        // First call succeeds (dep-check provider); second call throws (label-clear provider).
        var issue = new IssueSummary { Identifier = "42", Title = "Issue 42", Labels = new[] { AgentLabels.Error } };
        var template = MakeTemplate();

        var depCheckProvider = new Mock<IIssueProvider>();
        _mockProviderFactory
            .SetupSequence(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(depCheckProvider.Object)                                          // call 1: dep-check — succeeds
            .Throws(new InvalidOperationException("Provider config is corrupt"));     // call 2: label-clear — throws

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);


        // Act
        var (success, error, _) = await _service.DispatchIssueAsync(issue, template, IssueProviders, RepoProviders, null);

        // Assert — error tuple returned, orchestration never reached
        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("agent:error", error, StringComparison.OrdinalIgnoreCase);

        // Orchestration must never be called — the label-clear failure must short-circuit
        _mockDispatchOrchestration.Verify(
            d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Orchestration must not proceed when label-clear throws");
    }
}
