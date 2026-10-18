using Moq;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for PrReviewDrawerService in isolation: PR loading, label filtering,
/// pagination, dispatch, and close-on-success behavior.
/// Each test constructs PrReviewDrawerService with only its own dependencies. Since spec 051 2.3b and
/// 2.3c the API lists and dispatches the pull requests (<see cref="IUiBrowseClient"/>,
/// <see cref="IUiDispatchClient"/>); its TemplateBrowseServiceTests and ManualDispatchServiceTests cover that.
/// </summary>
public class PrReviewDrawerServiceTests
{
    private readonly Mock<IUiBrowseClient> _mockBrowse;
    private readonly Mock<IUiDispatchClient> _mockDispatch;
    private readonly PrReviewDrawerService _service;

    public PrReviewDrawerServiceTests()
    {
        _mockBrowse = new Mock<IUiBrowseClient>();
        _mockBrowse.Setup(b => b.GetLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _mockDispatch = new Mock<IUiDispatchClient>();

        _service = new PrReviewDrawerService(_mockBrowse.Object, _mockDispatch.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrReviewDrawerService>.Instance);
    }

    private static PipelineJobTemplate MakeTemplate(string id = "t-1") =>
        new() { Id = id, Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static PullRequestSummary MakePr(string id = "5") =>
        new() { Identifier = id, Number = int.Parse(id), Title = "PR " + id, BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Description = "", Labels = Array.Empty<string>(), IsDraft = false };

    // ── Independent construction ──

    [Fact]
    public void CanBeConstructed_WithoutIPipelineLoopService_IProjectStore_IConfigurationStore()
    {
        var svc = new PrReviewDrawerService(new Mock<IUiBrowseClient>().Object, new Mock<IUiDispatchClient>().Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrReviewDrawerService>.Instance);
        Assert.NotNull(svc);
        svc.Dispose();
    }

    // ── LoadPrDrawerPageAsync ──

    [Fact]
    public async Task LoadPrDrawerPageAsync_LoadsData_WhenRepoProviderExists()
    {
        var template = MakeTemplate();
        SetupRepoProvider(items: new[] { MakePr("1"), MakePr("2") }, hasMore: true);

        var error = await _service.LoadPrDrawerPageAsync(template, 1);

        Assert.Null(error);
        Assert.Equal(2, _service.DrawerState.Items.Count);
        Assert.True(_service.DrawerState.HasMore);
        Assert.Equal(1, _service.DrawerState.Page);
    }

    [Fact]
    public async Task LoadPrDrawerPageAsync_ReturnsTheApisReasonAndEmptyList_WhenItRefuses()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetPullRequestsAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Repository provider not found for this template."));

        var error = await _service.LoadPrDrawerPageAsync(template, 1);

        Assert.Equal("Repository provider not found for this template.", error);
        Assert.Empty(_service.DrawerState.Items);
    }

    // ── ClearPrDrawerLabelFilter ── (also covers pre-existing bug fix: must clear Items)

    [Fact]
    public async Task ClearPrDrawerLabelFilter_ClearsItemsAndPage()
    {
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
    public async Task DispatchPrReviewAsync_DispatchesThePrNumber_AndShowsTheApisMessage()
    {
        _mockDispatch.Setup(d => d.DispatchPullRequestAsync("t-1", 3363, It.IsAny<CancellationToken>()))
            .ReturnsAsync("⏳ Queued PR #3363 for review — the job controller will start an agent pod for it");

        var (success, error, msg) = await _service.DispatchPrReviewAsync(MakePr("3363"), MakeTemplate());

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("⏳ Queued PR #3363 for review — the job controller will start an agent pod for it", msg);
    }

    [Fact]
    public async Task DispatchPrReviewAsync_ReturnsTheApisReason_WhenItRefuses()
    {
        _mockDispatch.Setup(d => d.DispatchPullRequestAsync("t-1", 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PR #5 is already being processed or queued."));

        var (success, error, _) = await _service.DispatchPrReviewAsync(MakePr(), MakeTemplate());

        Assert.False(success);
        Assert.Equal("PR #5 is already being processed or queued.", error);
    }

    // ── DispatchFromPrDrawerAsync ──

    [Fact]
    public async Task DispatchFromPrDrawerAsync_ReturnsError_WhenTemplateIsNull()
    {
        var (success, error, _) = await _service.DispatchFromPrDrawerAsync(MakePr());

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.DrawerState.IsDispatching);
        _mockDispatch.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchFromPrDrawerAsync_DoesNotCloseDrawer_OnSuccess()
    {
        // PR drawer stays open after dispatch (unlike issue/epic)

        var template = MakeTemplate();
        SetupRepoProvider(items: new[] { MakePr() });
        await _service.OpenPrDrawerAsync("t-1", new[] { template });
        Assert.True(_service.DrawerState.IsOpen);
        _mockDispatch.Setup(d => d.DispatchPullRequestAsync("t-1", 5, It.IsAny<CancellationToken>())).ReturnsAsync("⏳ Queued PR #5 for review");

        var (success, _, _) = await _service.DispatchFromPrDrawerAsync(MakePr());

        Assert.True(success);
        Assert.True(_service.DrawerState.IsOpen); // PR drawer stays open
    }

    // ── Hide / ClosePrDrawer ──

    [Fact]
    public async Task ClosePrDrawer_NullsTemplate()
    {
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

    /// <summary>Makes the API return <paramref name="items"/> as the template's pull requests.</summary>
    private void SetupRepoProvider(IEnumerable<PullRequestSummary>? items = null, bool hasMore = false) =>
        _mockBrowse.Setup(b => b.GetPullRequestsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = (items ?? Enumerable.Empty<PullRequestSummary>()).ToList(),
                HasMore = hasMore, Page = 1, PageSize = 15
            });
}
