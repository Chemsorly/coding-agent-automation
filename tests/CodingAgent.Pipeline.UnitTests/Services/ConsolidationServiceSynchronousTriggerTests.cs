using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Acceptance criterion tests for issue #3026 — synchronous dispatch in
/// <see cref="ConsolidationService.TriggerAsync"/>.
/// <para>
/// These tests verify:
/// (A) A valid trigger creates exactly one <c>Pending</c> WorkItem and persists a <c>Pending</c> run.
/// (B) A config-error trigger (permanent 422) creates no WorkItem and persists no run.
/// </para>
/// </summary>
public sealed class ConsolidationServiceSynchronousTriggerTests
{
    private static readonly string[] SelectorLabels = ["dotnet", "kiro", "dotnet10"];

    // ── Shared mocks ──────────────────────────────────────────────────────────
    private readonly Mock<IConsolidationRunStore> _mockRunStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<IConsolidationSelectorResolver> _mockSelectorResolver = new();

    // Template used across all tests
    private static readonly PipelineJobTemplate Template = new()
    {
        Id = "tmpl-sync-1",
        Name = "Sync Test Repo",
        IssueProviderId = "ip-sync",
        RepoProviderId = "rp-sync",
        BrainProviderId = "bp-sync",
        Enabled = true
    };

    public ConsolidationServiceSynchronousTriggerTests()
    {
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>());

        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { Template.Id }
                }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { Template });

        // Default: selector resolver returns valid labels
        _mockSelectorResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<ProviderConfig?>(),
                It.IsAny<PipelineConfiguration>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)SelectorLabels);

        // Default: run store operations succeed
        _mockRunStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ConsolidationService CreateSut(PipelineConfiguration? config = null)
    {
        var cfg = config ?? new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath(),
            AgentTimeout = TimeSpan.FromMinutes(30)
        };

        return new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            cfg,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            _mockRunStore.Object,
            new Mock<IHarnessSuggestionStore>().Object,
            new Mock<IProviderConfigStore>().Object,
            WorkspaceManager: new ConsolidationWorkspaceManager(
                new LoggerConfiguration().CreateLogger(), cfg),
            WorkDistributor: _mockWorkDistributor.Object,
            SelectorResolver: _mockSelectorResolver.Object));
    }

    // ── Test A: Valid trigger creates exactly one Pending WorkItem ────────────

    /// <summary>
    /// Acceptance criterion (issue #3026, Test A):
    /// A valid trigger creates exactly one <c>Pending</c> WorkItem via <c>IWorkDistributor</c>
    /// and persists a <c>ConsolidationRun</c> with <c>Status = Pending</c>.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ValidTrigger_CreatesExactlyOnePendingWorkItem()
    {
        // Arrange: distributor returns success
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-acc-1", ErrorMessage: null));

        var sut = CreateSut();

        // Act
        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        // Assert: run is created with Pending status
        run.Should().NotBeNull("TriggerAsync must return a ConsolidationRun on success");
        run!.Status.Should().Be(ConsolidationRunStatus.Pending,
            "the run must start as Pending since the WorkItem was immediately submitted to the queue");
        run.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        run.TemplateId.Should().Be(Template.Id);
        run.TemplateName.Should().Be(Template.Name);
        run.RunId.Should().NotBeNullOrEmpty("each run must have a unique identifier");

        // Assert: IWorkDistributor.DistributeAsync was called exactly once
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the WorkItem must be created via IWorkDistributor.DistributeAsync exactly once");

        // Assert: the request was correctly built for a consolidation job
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.TaskType == WorkItemTaskType.Consolidation &&
                    r.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId &&
                    r.RepoProviderConfigId == Template.RepoProviderId &&
                    r.BrainProviderConfigId == Template.BrainProviderId &&
                    r.ConsolidationRunType == ConsolidationRunType.BrainConsolidation &&
                    r.ConsolidationTemplateId == Template.Id),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the JobDistributionRequest must carry correct consolidation-specific fields");

        // Assert: run was persisted to the store exactly once
        _mockRunStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r => r.Status == ConsolidationRunStatus.Pending),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the Pending run must be persisted to the store exactly once");
        // TODO [WARNING]: The It.Is predicate above checks Status==Pending but not r.RunId == run!.RunId.
        // If TriggerAsync saved a different (e.g. placeholder) run and returned a different object the
        // assertion would still pass. Add r.RunId == run!.RunId to the predicate to verify identity.
        // (review-findings-testqualityreviewer.md)
        // TODO [WARNING]: No assertion verifies the IssueIdentifier field of the JobDistributionRequest.
        // TriggerAsync now sets IssueIdentifier = type.ToString() (changed from run.RunId in the old
        // dispatcher). A regression where IssueIdentifier reverts to an unexpected value would not be
        // caught. Consider adding an It.Is check for IssueIdentifier to the DistributeAsync verify above.
        // (review-findings-testqualityreviewer.md)
    }

    // ── Test B: Config-error trigger creates no WorkItem ─────────────────────

    /// <summary>
    /// Acceptance criterion (issue #3026, Test B):
    /// When <c>IWorkDistributor.DistributeAsync</c> returns a permanent failure (e.g. no job
    /// template for the resolved agent selector — the 422 scenario), <c>TriggerAsync</c>
    /// must return <c>null</c> and persist no <c>ConsolidationRun</c> row.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ConfigError_CreatesNoWorkItemAndSurfacesPermanentFailure()
    {
        // Arrange: distributor returns permanent failure (simulates 422 from the API)
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(
                Success: false,
                WorkItemId: null,
                ErrorMessage: "no job template for agent selector",
                IsPermanentFailure: true));

        var sut = CreateSut();

        // Act
        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        // Assert: TriggerAsync returns null (no run created)
        run.Should().BeNull(
            "a permanent dispatch failure must not create a ConsolidationRun");

        // Assert: IWorkDistributor.DistributeAsync was called exactly once (the attempt was made)
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the dispatch must still be attempted even when it will fail");
        // TODO [WARNING]: This Times.Once verify is evaluated before the mock is reconfigured for the
        // retry path below. It cannot prevent a second DistributeAsync call during the failure scenario
        // itself if one were ever added, because the verify runs before the retry mock setup and second
        // TriggerAsync call. Consider splitting this test into two separate tests: one that asserts the
        // failure behaviour (null return, never saved), and one that independently asserts the dedup key
        // is cleared and re-trigger succeeds. Combining both in one method makes diagnosis harder if
        // either half fails for an unexpected reason. (review-findings-testqualityreviewer.md)

        // Assert: NO run was saved to the store
        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a permanent dispatch failure must record no ConsolidationRun row");

        // Assert: the dedup map is clear — re-triggering after fixing config must succeed
        // This is verified by a subsequent successful trigger returning a non-null run.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-retry-1", ErrorMessage: null));

        var retryRun = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        retryRun.Should().NotBeNull(
            "after a permanent failure the dedup key must be cleared, allowing a re-trigger");
        retryRun!.Status.Should().Be(ConsolidationRunStatus.Pending);
    }

    // ── Additional edge cases ─────────────────────────────────────────────────

    /// <summary>
    /// Transient failure (non-permanent) also returns null and creates no run row.
    /// The caller must re-trigger — there is no background retry sweep.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_TransientFailure_CreatesNoRunAndAllowsRetrigger()
    {
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(
                Success: false,
                WorkItemId: null,
                ErrorMessage: "capacity limit reached",
                IsPermanentFailure: false));

        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        run.Should().BeNull("transient failure must not create a run");

        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no run row must be persisted on transient failure");

        // Verify the dedup key was NOT retained — a re-trigger can succeed immediately
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-2", ErrorMessage: null));

        var retrigger = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        retrigger.Should().NotBeNull("after transient failure the key must be clear for re-trigger");
    }

    /// <summary>
    /// When selector resolver returns null (startup race / no profiles), TriggerAsync returns
    /// null without calling the distributor at all.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_NoProfiles_ReturnsNullWithoutCallingDistributor()
    {
        _mockSelectorResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<ProviderConfig?>(),
                It.IsAny<PipelineConfiguration>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>?)null);

        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        run.Should().BeNull("no profiles means transient failure — no dispatch attempted");

        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "distributor must not be called when selector resolution fails");

        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A duplicate trigger while a run is already Pending must be rejected.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DuplicateWhilePending_ReturnsNull()
    {
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-dup-1", ErrorMessage: null));

        var sut = CreateSut();

        // First trigger succeeds
        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);
        first.Should().NotBeNull("first trigger must succeed");

        // Second trigger for same (type, templateId) must be rejected
        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);
        second.Should().BeNull("duplicate trigger while run is Pending must be rejected");

        // Distributor called only once (second trigger rejected before reaching distributor)
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "distributor must not be called on duplicate trigger");
    }
}
