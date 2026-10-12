using Moq;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for EpicDrawerService in isolation: epic loading with deduplication,
/// label filtering, dispatch, close-on-success behavior.
/// Each test constructs EpicDrawerService with only its own dependencies. The API picks the phase and
/// the epic tracker (spec 051 2.3c); its TemplateDispatchServiceTests cover that.
/// </summary>
public class EpicDrawerServiceTests
{
    private static readonly string[] EpicIds = ["100", "101"];
    private static readonly string[] BackendLabel = ["backend"];

    private readonly Mock<IUiBrowseClient> _mockBrowse;
    private readonly Mock<IUiDispatchClient> _mockDispatch;
    private readonly EpicDrawerService _service;

    public EpicDrawerServiceTests()
    {
        _mockBrowse = new Mock<IUiBrowseClient>();
        _mockBrowse.Setup(b => b.GetEpicLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _mockDispatch = new Mock<IUiDispatchClient>();

        _service = new EpicDrawerService(_mockBrowse.Object, _mockDispatch.Object);
    }

    private static PipelineJobTemplate MakeTemplate(string id = "t-1") =>
        new() { Id = id, Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeEpic(string id, params string[] labels) =>
        new() { Identifier = id, Title = "Epic " + id, Labels = labels };

    // ── Independent construction ──

    [Fact]
    public void CanBeConstructed_WithoutIPipelineLoopService_IProjectStore_IConfigurationStore()
    {
        var svc = new EpicDrawerService(new Mock<IUiBrowseClient>().Object, new Mock<IUiDispatchClient>().Object);
        Assert.NotNull(svc);
        svc.Dispose();
    }

    // ── LoadEpicDrawerIssuesAsync ──
    // The API merges the new and the approved epics and picks the epic tracker; its
    // TemplateBrowseServiceTests cover that. Here: the drawer shows what the API returns.

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_ShowsTheApisEpics_WithTheSelectedLabels()
    {
        var template = MakeTemplate();
        _service.DrawerState.SelectedLabels.Add("backend");
        IReadOnlyList<string>? capturedLabels = null;
        _mockBrowse.Setup(b => b.GetEpicsAsync("t-1", 1, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary> { MakeEpic("100", "agent:epic"), MakeEpic("101", "agent:epic-approved") },
                HasMore = true, Page = 1, PageSize = 8,
            });

        var error = await _service.LoadEpicDrawerIssuesAsync(template, 1);

        Assert.Null(error);
        Assert.Equal(EpicIds, _service.DrawerState.Items.Select(i => i.Identifier));
        Assert.True(_service.DrawerState.HasMore);
        Assert.Equal(BackendLabel, capturedLabels);
    }

    [Fact]
    public async Task LoadEpicDrawerIssuesAsync_ReturnsTheApisReason_WhenItRefuses()
    {
        var template = MakeTemplate();
        _mockBrowse.Setup(b => b.GetEpicsAsync("t-1", It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Epic issue provider not found."));

        var error = await _service.LoadEpicDrawerIssuesAsync(template, 1);

        Assert.Equal("Epic issue provider not found.", error);
        Assert.Empty(_service.DrawerState.Items);
    }

    [Fact]
    public async Task OpenEpicDrawerAsync_LoadsTheEpicTrackersLabels()
    {
        SetupIssueProviderForEpic();
        _mockBrowse.Setup(b => b.GetEpicLabelsAsync("t-1", It.IsAny<CancellationToken>())).ReturnsAsync(BackendLabel);

        await _service.OpenEpicDrawerAsync("t-1", new[] { MakeTemplate() });

        Assert.Equal(BackendLabel, _service.DrawerState.Labels);
    }

    // ── DispatchDecompositionAsync ──

    [Fact]
    public async Task DispatchDecompositionAsync_DispatchesTheEpic_AndShowsTheApisMessage()
    {
        _mockDispatch.Setup(d => d.DispatchEpicAsync("t-1", "e-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("⏳ Queued epic #e-1 for analysis — the job controller will start an agent pod for it");

        var (success, error, msg) = await _service.DispatchDecompositionAsync(MakeEpic("e-1", "agent:epic"), MakeTemplate());

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("⏳ Queued epic #e-1 for analysis — the job controller will start an agent pod for it", msg);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_ReturnsTheApisReason_WhenItRefuses()
    {
        _mockDispatch.Setup(d => d.DispatchEpicAsync("t-1", "e-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The project's epic issue provider no longer exists."));

        var (success, error, _) = await _service.DispatchDecompositionAsync(MakeEpic("e-1"), MakeTemplate());

        Assert.False(success);
        Assert.Equal("The project's epic issue provider no longer exists.", error);
    }

    // ── DispatchFromEpicDrawerAsync ──

    [Fact]
    public async Task DispatchFromEpicDrawerAsync_ClosesDrawer_OnSuccess()
    {
        var template = MakeTemplate();
        SetupIssueProviderForEpic(items: new[] { MakeEpic("e-1", "agent:epic") });
        await _service.OpenEpicDrawerAsync("t-1", new[] { template });
        Assert.True(_service.DrawerState.IsOpen);
        _mockDispatch.Setup(d => d.DispatchEpicAsync("t-1", "e-1", It.IsAny<CancellationToken>())).ReturnsAsync("⏳ Queued epic #e-1 for analysis");

        var (success, _, _) = await _service.DispatchFromEpicDrawerAsync(MakeEpic("e-1"));

        Assert.True(success);
        Assert.False(_service.DrawerState.IsOpen);
    }

    [Fact]
    public async Task DispatchFromEpicDrawerAsync_ReturnsError_WhenTemplateIsNull()
    {
        var (success, error, _) = await _service.DispatchFromEpicDrawerAsync(MakeEpic("e-1"));

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.DrawerState.IsDispatching);
        _mockDispatch.VerifyNoOtherCalls();
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

    /// <summary>Makes the API return <paramref name="items"/> as the template's epics.</summary>
    private void SetupIssueProviderForEpic(IEnumerable<IssueSummary>? items = null) =>
        _mockBrowse.Setup(b => b.GetEpicsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = (items ?? Enumerable.Empty<IssueSummary>()).ToList(),
                HasMore = false, Page = 1, PageSize = 8
            });
}
