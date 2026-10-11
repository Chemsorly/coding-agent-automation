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

        // Assert: run is created successfully
        run.Should().NotBeNull("TriggerAsync must return a ConsolidationTriggerResult on success");
        // Status (ConsolidationRunStatus) was removed in issue #3032 — success is implied by non-null return.
        // TODO [WARNING]: WorkItemId (populated from the mock DistributionResult) is not asserted here.
        // A re-trigger returning a ConsolidationTriggerResult with a null WorkItemId would still pass.
        // Add run!.WorkItemId.Should().NotBeNullOrEmpty() to confirm WorkItemId propagation is wired correctly.
        run!.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
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
                    r.ConsolidationTemplateId == Template.Id &&
                    r.ConsolidationTemplateName == Template.Name),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the JobDistributionRequest must carry correct consolidation-specific fields");

        // Assert: IssueIdentifier uses the deterministic {type}:{templateId} format (issue #3027)
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == $"{ConsolidationRunType.BrainConsolidation}:{Template.BrainProviderId}"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            $"IssueIdentifier must be '{ConsolidationRunType.BrainConsolidation}:{Template.BrainProviderId}' for cross-replica dedup (issue #3027): one brain consolidation per brain");

        // Structural enforcement: IConsolidationRunStore is no longer a dependency of ConsolidationService
        // (removed in issue #3031), so store writes cannot occur regardless of trigger outcome.
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
        // Status (ConsolidationRunStatus) was removed in issue #3032 — success is implied by non-null return.
        // TODO [WARNING]: Assertion is too weak — only non-null is checked. A ConsolidationTriggerResult with
        // empty RunId or wrong Type would still pass. Add retryRun!.RunId.Should().NotBeNullOrEmpty() and
        // retryRun.Type.Should().Be(ConsolidationRunType.BrainConsolidation) to match Test A's happy-path pattern.
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

    /// <summary>
    /// When IWorkDistributor.DistributeAsync throws an unexpected exception,
    /// TriggerAsync must catch it, log an error, and return null.
    /// This covers the catch (Exception ex) block at lines 303-309.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenDistributorThrows_ReturnsNullAndDoesNotPropagate()
    {
        // Arrange: distributor throws an unexpected exception
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network timeout"));

        var sut = CreateSut();

        // Act: must not throw — exception is caught internally
        var act = async () => await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            "TriggerAsync must swallow unexpected distributor exceptions and return null");

        // TODO [WARNING]: The second TriggerAsync call below is redundant — act.Should().NotThrowAsync()
        // already exercises the same code path (same mock, same sut state). The return-value assertion
        // cannot be inferred from NotThrowAsync alone (it discards the return value), so a second call
        // is used to capture it, but this invokes DistributeAsync a second time on the same always-
        // throwing mock with no state reset. The same behaviour is already covered more precisely by
        // TriggerAsync_WhenDistributeAsyncThrows_ReturnsNull in ConsolidationServiceTests. Consider
        // either calling act() and extracting the return value from the first invocation, or removing
        // this test in favour of the equivalent in ConsolidationServiceTests.
        var result = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        result.Should().BeNull(
            "when DistributeAsync throws, TriggerAsync must return null (not a ConsolidationRun)");
    }
}
