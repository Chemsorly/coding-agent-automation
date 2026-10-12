using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for EpicDrawerService in isolation: epic loading with deduplication,
/// label filtering, dispatch phase selection, close-on-success behavior.
/// Each test constructs EpicDrawerService with only its own dependencies.
/// </summary>
public class EpicDrawerServiceTests
{
    private static readonly string[] EpicIds = ["100", "101"];
    private static readonly string[] BackendLabel = ["backend"];

    private readonly Mock<IUiBrowseClient> _mockBrowse;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly EpicDrawerService _service;

    private static readonly IReadOnlyList<ProviderConfig> IssueProviders =
        new List<ProviderConfig> { new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" } };

    private static readonly IReadOnlyList<ProviderConfig> RepoProviders =
        new List<ProviderConfig> { new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" } };

    public EpicDrawerServiceTests()
    {
        _mockBrowse = new Mock<IUiBrowseClient>();
        _mockBrowse.Setup(b => b.GetEpicLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();

        _service = new EpicDrawerService(
            _mockBrowse.Object,
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
            new Mock<IUiBrowseClient>().Object,
            new Mock<IDispatchOrchestrationService>().Object);
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

    // ── DispatchDecompositionAsync — phase type selection ──

    [Fact]
    public async Task DispatchDecompositionAsync_UsesDecompositionAnalysis_ForRegularEpic()
    {
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

    // ── DispatchDecompositionAsync — binding to the tracker the epic was listed from ──

    [Fact]
    public async Task DispatchDecompositionAsync_ProjectWithEpicTracker_BindsRunToEpicTracker()
    {
        // The drawer lists the project's epic tracker, so the run must be bound to it,
        // while the drawer's template executes the epic.
        var issueProviders = new List<ProviderConfig>
        {
            IssueProviders[0],
            new() { Id = "epic-ip", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Epics" }
        };
        var project = new PipelineProject { Id = "p-1", Name = "P", TemplateIds = new[] { "t-1" }, EpicIssueProviderId = "epic-ip" };
        var capturedRequest = CaptureDecompositionRequest();

        var (success, _, _) = await _service.DispatchDecompositionAsync(
            MakeEpic("e-1", "agent:epic"), MakeTemplate(), issueProviders, RepoProviders, project);

        Assert.True(success);
        Assert.Equal("epic-ip", capturedRequest()!.IssueProviderId.Value);
        Assert.Equal("rp-1", capturedRequest()!.RepoProviderId.Value);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_ProjectWithoutEpicTracker_BindsRunToTemplateTracker()
    {
        var project = new PipelineProject { Id = "p-1", Name = "P", TemplateIds = new[] { "t-1" } };
        var capturedRequest = CaptureDecompositionRequest();

        var (success, _, _) = await _service.DispatchDecompositionAsync(
            MakeEpic("e-1", "agent:epic"), MakeTemplate(), IssueProviders, RepoProviders, project);

        Assert.True(success);
        Assert.Equal("ip-1", capturedRequest()!.IssueProviderId.Value);
    }

    [Fact]
    public async Task DispatchDecompositionAsync_ReturnsError_WhenEpicTrackerMissing()
    {
        var project = new PipelineProject { Id = "p-1", Name = "P", TemplateIds = new[] { "t-1" }, EpicIssueProviderId = "deleted-ip" };

        var (success, error, _) = await _service.DispatchDecompositionAsync(
            MakeEpic("e-1", "agent:epic"), MakeTemplate(), IssueProviders, RepoProviders, project);

        Assert.False(success);
        Assert.Contains("epic issue provider no longer exists", error);
    }

    private Func<DecompositionDispatchOrchestrationRequest?> CaptureDecompositionRequest()
    {
        DecompositionDispatchOrchestrationRequest? capturedRequest = null;
        _mockDispatchOrchestration.Setup(d => d.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecompositionDispatchOrchestrationRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(CreateMinimalRequest(WorkItemTaskType.Decomposition));
        _mockDispatchOrchestration.Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));
        return () => capturedRequest;
    }

    // ── DispatchFromEpicDrawerAsync ──

    [Fact]
    public async Task DispatchFromEpicDrawerAsync_ClosesDrawer_OnSuccess()
    {
        var template = MakeTemplate();
        SetupIssueProviderForEpic(items: new[] { MakeEpic("e-1", "agent:epic") });
        await _service.OpenEpicDrawerAsync("t-1", new[] { template });
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

    /// <summary>Makes the API return <paramref name="items"/> as the template's epics.</summary>
    private void SetupIssueProviderForEpic(IEnumerable<IssueSummary>? items = null) =>
        _mockBrowse.Setup(b => b.GetEpicsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = (items ?? Enumerable.Empty<IssueSummary>()).ToList(),
                HasMore = false, Page = 1, PageSize = 8
            });

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
