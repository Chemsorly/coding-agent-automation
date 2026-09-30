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
/// Updated for issue #3028: TriggerAsync no longer persists to the ConsolidationRuns store.
/// <para>
/// These tests verify:
/// (A) A valid trigger creates exactly one <c>Pending</c> WorkItem via <c>IWorkDistributor</c>.
/// (B) A config-error trigger (permanent failure) creates no WorkItem and returns null.
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

        // Default: run store operations succeed (though they should not be called for TriggerAsync after #3028)
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
            WorkDistributor: _mockWorkDistributor.Object,
            SelectorResolver: _mockSelectorResolver.Object));
    }

    // ── Test A: Valid trigger creates exactly one Pending WorkItem ────────────

    /// <summary>
    /// Acceptance criterion (issue #3026, Test A — updated for issue #3028):
    /// A valid trigger creates exactly one WorkItem via IWorkDistributor
    /// and returns a ConsolidationRun with Status = Pending.
    /// After issue #3028: TriggerAsync no longer persists to the ConsolidationRuns store.
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

        // Issue #3028: TriggerAsync no longer persists to the ConsolidationRuns store.
        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "TriggerAsync must not write to the ConsolidationRuns store (issue #3028)");

        // Assert: IssueIdentifier uses the deterministic {type}:{templateId} format (issue #3027)
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == $"{ConsolidationRunType.BrainConsolidation}:{Template.BrainProviderId}"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            $"IssueIdentifier must be '{ConsolidationRunType.BrainConsolidation}:{Template.BrainProviderId}' for cross-replica dedup (issue #3027): one brain consolidation per brain");
    }

    // ── Test B: Config-error trigger creates no WorkItem ─────────────────────

    /// <summary>
    /// Acceptance criterion (issue #3026, Test B — updated for issue #3028):
    /// When IWorkDistributor.DistributeAsync returns a permanent failure,
    /// TriggerAsync must return null. No store writes occur (issue #3028).
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ConfigError_CreatesNoWorkItemAndSurfacesPermanentFailure()
    {
        // Arrange: distributor returns permanent failure
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

        // Assert: TriggerAsync returns null
        run.Should().BeNull("a permanent dispatch failure must not create a ConsolidationRun");

        // Assert: IWorkDistributor.DistributeAsync was called exactly once
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the dispatch must still be attempted even when it will fail");

        // Issue #3028: no store writes on failure
        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "TriggerAsync must not write to the store (issue #3028)");
        _mockRunStore.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no rollback needed since nothing was persisted");

        // Verify re-triggering after fixing config must succeed
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-retry-1", ErrorMessage: null));

        var retryRun = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        retryRun.Should().NotBeNull(
            "after a permanent failure the dedup key must be clear, allowing a re-trigger");
        retryRun!.Status.Should().Be(ConsolidationRunStatus.Pending);
    }

    // ── Additional edge cases ─────────────────────────────────────────────────

    /// <summary>
    /// Transient failure returns null. No store writes (issue #3028).
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

        // Issue #3028: no store writes on failure
        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "TriggerAsync must not write to the store (issue #3028)");
        _mockRunStore.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Verify re-triggering can succeed
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-2", ErrorMessage: null));

        var retrigger = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        retrigger.Should().NotBeNull("after transient failure, re-trigger must succeed");
    }

    /// <summary>
    /// When selector resolver returns null (startup race), TriggerAsync returns null without calling distributor.
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
    /// After issue #3027: dedup is API-layer — both triggers call DistributeAsync.
    /// The second trigger receives DistributionResult(Success=true, WorkItemId=null, AlreadyExists=true) which
    /// signals a 409 from the partial unique index. TriggerAsync maps this to null.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DuplicateWhilePending_ReturnsNull()
    {
        _mockWorkDistributor
            .SetupSequence(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-dup-1", ErrorMessage: null, Queued: true))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: null, ErrorMessage: null, Queued: true, AlreadyExists: true));

        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);
        first.Should().NotBeNull("first trigger must succeed");

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);
        second.Should().BeNull("duplicate trigger must be rejected (WorkItemId=null = 409)");

        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "both triggers call DistributeAsync; dedup is enforced by the API layer");
    }
}
