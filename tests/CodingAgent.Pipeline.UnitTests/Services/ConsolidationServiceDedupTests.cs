using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Acceptance criterion tests for issue #3027 — DB-layer dedup via deterministic IssueIdentifier.
///
/// After removing the in-memory <c>_runningRuns</c> dictionary, duplicate detection is
/// delegated to the API-layer partial unique index on (IssueIdentifier, IssueProviderConfigId).
/// <see cref="KubernetesWorkDistributor"/> maps a 409 Conflict to
/// <c>DistributionResult(Success=true, WorkItemId=null, Queued=true)</c>.
/// <see cref="ConsolidationService.TriggerAsync"/> detects <c>WorkItemId == null</c> as the
/// duplicate-rejection signal, rolls back the second persisted run, and returns null.
/// </summary>
public sealed class ConsolidationServiceDedupTests
{
    private static readonly string[] SelectorLabels = ["dotnet", "kiro", "dotnet10"];

    private readonly Mock<IConsolidationRunStore> _mockRunStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory = new();
    private readonly Mock<IConsolidationSelectorResolver> _mockSelectorResolver = new();

    private static readonly PipelineJobTemplate Template = new()
    {
        Id = "tmpl-dedup-1",
        Name = "Dedup Test Repo",
        IssueProviderId = "ip-dedup",
        RepoProviderId = "rp-dedup",
        BrainProviderId = "bp-dedup",
        Enabled = true
    };

    public ConsolidationServiceDedupTests()
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

        _mockSelectorResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<ProviderConfig?>(),
                It.IsAny<PipelineConfiguration>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)SelectorLabels);

        // Default store operations succeed
        _mockRunStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRunStore
            .Setup(s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ConsolidationService CreateSut(Mock<IWorkDistributor> mockDistributor)
    {
        var cfg = new PipelineConfiguration
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
            WorkDistributor: mockDistributor.Object,
            SelectorResolver: _mockSelectorResolver.Object));
    }

    /// <summary>
    /// Acceptance criterion (issue #3027):
    /// A second trigger for the same (type, templateId) while the first is non-terminal
    /// reports "already running" (returns null) and creates no second WorkItem.
    ///
    /// Mechanism: KubernetesWorkDistributor maps 409 Conflict →
    ///   DistributionResult(Success=true, WorkItemId=null, Queued=true).
    /// ConsolidationService.TriggerAsync detects WorkItemId==null as the duplicate-rejection
    /// signal, rolls back the second persisted run, and returns null.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_SecondTrigger_WhileFirstNonTerminal_ReturnsNull_NoSecondWorkItem()
    {
        // Arrange: first trigger succeeds (WorkItem created, WorkItemId="wi-1");
        // second trigger receives (Success=true, WorkItemId=null) simulating the 409 path.
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor
            .SetupSequence(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(
                Success: true,
                WorkItemId: "wi-dedup-first",
                ErrorMessage: null,
                Queued: true))
            .ReturnsAsync(new DistributionResult(
                Success: true,
                WorkItemId: null,       // 409 path — partial unique index rejected the duplicate
                ErrorMessage: null,
                Queued: true));

        var sut = CreateSut(mockDistributor);

        // Act
        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        // Assert: first trigger succeeds, second is rejected as "already running"
        first.Should().NotBeNull("first trigger must succeed");
        first!.Status.Should().Be(ConsolidationRunStatus.Pending);
        first.WorkItemId.Should().Be("wi-dedup-first", "WorkItemId must be populated from DistributionResult");

        second.Should().BeNull(
            "second trigger while first is non-terminal must be rejected (WorkItemId=null = 409 duplicate)");

        // Assert: both triggers reach the API layer — no in-process short-circuit
        mockDistributor.Verify(
            d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "both triggers call DistributeAsync; dedup is enforced by the API-layer partial unique index");

        // Assert: two optimistic persists (one per trigger), one rollback for the second
        // TODO [WARNING]: This verify uses Times.AtLeast(2) without pinning which run IDs were saved.
        // A scenario where the WorkItemId re-persist (step 9) is accidentally skipped on the first run
        // would still pass because both initial persists satisfy AtLeast(2). Consider splitting into:
        // (1) SaveRunAsync for runs matching first.RunId expecting Times.AtLeast(2), plus
        // (2) an explicit assertion that first.WorkItemId is non-null (already present at line 168).
        // (review-findings-testqualityreviewer.md)
        _mockRunStore.Verify(
            s => s.SaveRunAsync(
                It.Is<ConsolidationRun>(r => r.Status == ConsolidationRunStatus.Pending),
                It.IsAny<CancellationToken>()),
            Times.AtLeast(2),
            "each TriggerAsync call optimistically persists a run before dispatch");

        _mockRunStore.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "only the second (duplicate-rejected) run must be rolled back via DeleteRunAsync");
    }

    /// <summary>
    /// A duplicate trigger for a global (null templateId) consolidation run must also be
    /// rejected via the WorkItemId==null detection path.
    /// IssueIdentifier for global runs is "{type}:global".
    /// </summary>
    [Fact]
    public async Task TriggerAsync_SecondTrigger_GlobalRun_WhileFirstNonTerminal_ReturnsNull()
    {
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor
            .SetupSequence(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(
                Success: true,
                WorkItemId: "wi-global-first",
                ErrorMessage: null,
                Queued: true))
            .ReturnsAsync(new DistributionResult(
                Success: true,
                WorkItemId: null,   // 409 duplicate
                ErrorMessage: null,
                Queued: true));

        var sut = CreateSut(mockDistributor);

        // Act: global run (null templateId) — HarnessSuggestions uses global scope
        var first = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions,
            null,
            CancellationToken.None);

        var second = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions,
            null,
            CancellationToken.None);

        // Assert
        first.Should().NotBeNull("first global trigger must succeed");
        second.Should().BeNull("second global trigger while first is non-terminal must be rejected");

        // Assert: IssueIdentifier for global run uses "{type}:global" format
        // TODO [WARNING]: Times.AtLeastOnce only verifies the first trigger's IssueIdentifier.
        // A regression that changes the identifier only on retries (e.g. falling back to type.ToString()
        // on the second call) would not be caught. Change to Times.Exactly(2) (mirroring the main
        // acceptance criterion test) to assert both calls carry the correct IssueIdentifier.
        // (review-findings-testqualityreviewer.md)
        mockDistributor.Verify(
            d => d.DistributeAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == $"{ConsolidationRunType.HarnessSuggestions}:global"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "global run IssueIdentifier must be 'HarnessSuggestions:global'");
    }

    /// <summary>
    /// Different (type, templateId) pairs must not interfere — a duplicate rejection for
    /// BrainConsolidation:tmpl must not block RefactoringDetection:tmpl.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DifferentTypes_SameTemplate_NoInterference()
    {
        // Both calls succeed independently (different IssueIdentifier values)
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(
                Success: true,
                WorkItemId: "wi-no-interference",
                ErrorMessage: null,
                Queued: true));

        var sut = CreateSut(mockDistributor);

        var brain = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId(Template.Id),
            CancellationToken.None);

        var refactoring = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection,
            new TemplateId(Template.Id),
            CancellationToken.None);

        brain.Should().NotBeNull("BrainConsolidation trigger must succeed");
        refactoring.Should().NotBeNull("RefactoringDetection trigger for same template must also succeed");

        // TODO [WARNING]: The mock distributor uses a fixed WorkItemId ("wi-no-interference") for all calls,
        // so both brain.WorkItemId and refactoring.WorkItemId are the same value. The test does not assert
        // that brain.RunId != refactoring.RunId, which is the more direct statement of "no interference"
        // (each trigger created an independent run). A regression that reused the same run object for both
        // triggers would still pass. Add:
        //   brain!.RunId.Should().NotBe(refactoring!.RunId, "each trigger must create an independent run");
        // Additionally, there is no SaveRunAsync verify confirming two distinct persists were made (one per run).
        // (review-findings-testqualityreviewer.md)

        // Each type uses a distinct IssueIdentifier
        mockDistributor.Verify(
            d => d.DistributeAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == $"{ConsolidationRunType.BrainConsolidation}:{Template.Id}"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "BrainConsolidation must use IssueIdentifier 'BrainConsolidation:{templateId}'");

        mockDistributor.Verify(
            d => d.DistributeAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == $"{ConsolidationRunType.RefactoringDetection}:{Template.Id}"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "RefactoringDetection must use IssueIdentifier 'RefactoringDetection:{templateId}'");
    }
}
