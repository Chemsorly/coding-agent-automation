using Moq;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for IssueDrawerService in isolation: issue loading, dependency checking,
/// label filtering, pagination, dispatch, active issue tracking, and CancellationToken lifecycle.
/// Each test constructs IssueDrawerService with only its own dependencies (no IPipelineLoopService,
/// IProjectStore, or IConfigurationStore), verifying independent testability. Since spec 051 2.3b and
/// 2.3c the API browses the providers and dispatches (<see cref="IUiBrowseClient"/>,
/// <see cref="IUiDispatchClient"/>); its TemplateBrowseServiceTests and ManualDispatchServiceTests cover that.
/// </summary>
public class IssueDrawerServiceTests
{
    private static readonly string[] BugAndDocsLabels = ["bug", "docs"];

    private readonly Mock<IUiBrowseClient> _mockBrowse;
    private readonly Mock<IUiDispatchClient> _mockDispatch;
    private readonly IssueDrawerService _service;

    public IssueDrawerServiceTests()
    {
        _mockBrowse = new Mock<IUiBrowseClient>();
        _mockDispatch = new Mock<IUiDispatchClient>();

        // Default stubs so opening the drawer does not throw in tests that do not care about it.
        _mockBrowse.Setup(b => b.GetActiveIssuesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UiActiveIssue>());
        _mockBrowse.Setup(b => b.GetLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockBrowse.Setup(b => b.GetIssueReadinessAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DependencyCheckResult>());

        _service = new IssueDrawerService(_mockBrowse.Object, _mockDispatch.Object);
    }

    private static PipelineJobTemplate MakeTemplate(string id = "t-1") =>
        new() { Id = id, Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeIssue(string id = "42", string? description = null) =>
        new() { Identifier = id, Title = "Issue " + id, Labels = Array.Empty<string>(), Description = description };

    // ── Independent construction ──

    [Fact]
    public void CanBeConstructed_WithoutIPipelineLoopService_IProjectStore_IConfigurationStore()
    {
        // This test verifies the acceptance criterion: independently injectable without unrelated deps
        var svc = new IssueDrawerService(new Mock<IUiBrowseClient>().Object, new Mock<IUiDispatchClient>().Object);
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
    public async Task DispatchIssueAsync_DispatchesTheTemplatesIssue_AndShowsTheApisMessage()
    {
        _mockDispatch.Setup(d => d.DispatchIssueAsync("t-1", "42", It.IsAny<CancellationToken>()))
            .ReturnsAsync("⏳ Queued #42 — the job controller will start an agent pod for it");

        var (success, error, msg) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate());

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("⏳ Queued #42 — the job controller will start an agent pod for it", msg);
    }

    [Fact]
    public async Task DispatchIssueAsync_ReturnsTheApisReason_WhenItRefuses()
    {
        _mockDispatch.Setup(d => d.DispatchIssueAsync("t-1", "42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cannot dispatch — issue is blocked by open dependencies: #5"));

        var (success, error, msg) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate());

        Assert.False(success);
        Assert.Equal("Cannot dispatch — issue is blocked by open dependencies: #5", error);
        Assert.Null(msg);
    }

    [Fact]
    public async Task DispatchIssueAsync_WhenTheApiCannotBeReached_SaysItCouldNotDispatch()
    {
        _mockDispatch.Setup(d => d.DispatchIssueAsync("t-1", "42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate());

        Assert.False(success);
        Assert.Equal("Could not dispatch — Connection refused", error);
    }

    [Fact]
    public async Task DispatchIssueAsync_WhenTheCallTimesOut_SaysItCouldNotDispatch()
    {
        _mockDispatch.Setup(d => d.DispatchIssueAsync("t-1", "42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request timed out."));

        var (success, error, _) = await _service.DispatchIssueAsync(MakeIssue(), MakeTemplate());

        Assert.False(success);
        Assert.Equal("Could not dispatch — The request timed out.", error);
    }

    // ── DispatchFromIssueDrawerAsync ──

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_ReturnsError_WhenTemplateIsNull()
    {
        var (success, error, _) = await _service.DispatchFromIssueDrawerAsync(MakeIssue());

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.DrawerState.IsDispatching);
        _mockDispatch.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_ClosesDrawer_OnSuccess()
    {
        var template = MakeTemplate();
        SetupIssueProvider(items: new[] { MakeIssue() });
        await _service.OpenIssueDrawerAsync("t-1", new[] { template });
        Assert.True(_service.DrawerState.IsOpen);
        _mockDispatch.Setup(d => d.DispatchIssueAsync("t-1", "42", It.IsAny<CancellationToken>())).ReturnsAsync("⏳ Queued #42");

        var (success, _, _) = await _service.DispatchFromIssueDrawerAsync(MakeIssue());

        Assert.True(success);
        Assert.False(_service.DrawerState.IsOpen);
        Assert.False(_service.DrawerState.IsDispatching);
    }

    [Fact]
    public async Task DispatchFromIssueDrawerAsync_KeepsTheDrawerOpen_WhenTheApiRefuses()
    {
        SetupIssueProvider(items: new[] { MakeIssue() });
        await _service.OpenIssueDrawerAsync("t-1", new[] { MakeTemplate() });
        _mockDispatch.Setup(d => d.DispatchIssueAsync("t-1", "42", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Could not dispatch — distribution failed."));

        var (success, error, _) = await _service.DispatchFromIssueDrawerAsync(MakeIssue());

        Assert.False(success);
        Assert.Equal("Could not dispatch — distribution failed.", error);
        Assert.True(_service.DrawerState.IsOpen);
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
}
