using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies ConsolidationService delegates to IConsolidationRunStore and IHarnessSuggestionStore
/// by using mocks. Ensures no filesystem I/O happens inside the service itself.
/// </summary>
public sealed class ConsolidationServiceStoreDelegationTests
{
    private readonly Mock<IConsolidationRunStore> _mockRunStore = new();
    private readonly Mock<IHarnessSuggestionStore> _mockHarnessStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory = new();

    public ConsolidationServiceStoreDelegationTests()
    {
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>());
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new List<string> { "t1", "t2" } }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "Template1", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true },
                new() { Id = "t2", Name = "Template2", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true }
            });
    }

    private ConsolidationService CreateSut()
    {
        // WorkDistributor returns success so TriggerAsync can proceed.
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-delegation-test", ErrorMessage: null));

        return new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            _mockRunStore.Object,
            _mockHarnessStore.Object,
            new Mock<IProviderConfigStore>().Object,
            WorkspaceManager: new ConsolidationWorkspaceManager(
                new LoggerConfiguration().CreateLogger(),
                new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() }),
            WorkDistributor: mockWorkDistributor.Object));
    }

    [Fact]
    public async Task UpdateRunAsync_Calls_GetByIdAsync_OnStore()
    {
        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
        _mockRunStore.Setup(s => s.GetByIdAsync((RunId)runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);

        var sut = CreateSut();
        await sut.UpdateRunAsync(runId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

        _mockRunStore.Verify(s => s.GetByIdAsync((RunId)runId, It.IsAny<CancellationToken>()), Times.Once);
        _mockRunStore.Verify(s => s.SaveRunAsync(It.Is<ConsolidationRun>(r =>
            r.RunId == runId && r.Status == ConsolidationRunStatus.Succeeded), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransitionToRunningAsync_Calls_GetByIdAsync_OnStore()
    {
        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.HarnessSuggestions,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        _mockRunStore.Setup(s => s.GetByIdAsync((RunId)runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);

        var sut = CreateSut();
        await sut.TransitionToRunningAsync(runId, CancellationToken.None);

        _mockRunStore.Verify(s => s.GetByIdAsync((RunId)runId, It.IsAny<CancellationToken>()), Times.Once);
        _mockRunStore.Verify(s => s.SaveRunAsync(It.Is<ConsolidationRun>(r =>
            r.Status == ConsolidationRunStatus.Running), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupOrphanedRunsAsync_Calls_LoadAllAndSave_OnStore()
    {
        var orphan = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { orphan });

        var sut = CreateSut();
        await sut.CleanupOrphanedRunsAsync([], CancellationToken.None);

        _mockRunStore.Verify(s => s.SaveRunAsync(It.Is<ConsolidationRun>(r =>
            r.RunId == orphan.RunId && r.Status == ConsolidationRunStatus.Failed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupOrphanedRunsAsync_SkipsRun_WhenAgentIsStillActive()
    {
        var orphan = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { orphan });

        var sut = CreateSut();
        // Pass the run's ID as an active agent job — it should NOT be marked Failed
        await sut.CleanupOrphanedRunsAsync([orphan.RunId], CancellationToken.None);

        _mockRunStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()), Times.Never,
            "A running run with an active agent should not be marked Failed");
    }

    [Fact]
    public async Task SaveHarnessSuggestionsAsync_Delegates_ToStore()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 5,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.9m,
            Suggestions = new List<HarnessSuggestion>()
        };

        var sut = CreateSut();
        await sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);

        _mockHarnessStore.Verify(s => s.SaveAsync(suggestions, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetHarnessSuggestionsAsync_Delegates_ToStore()
    {
        var expected = new HarnessSuggestions
        {
            BasedOnRunCount = 3,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.7m,
            Suggestions = new List<HarnessSuggestion>()
        };
        _mockHarnessStore.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var result = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        result.Should().BeSameAs(expected);
        _mockHarnessStore.Verify(s => s.LoadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteRunAsync_Calls_DeleteRunAsync_OnStore()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        _mockRunStore.Setup(s => s.DeleteRunAsync(runId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.DeleteRunAsync(runId, CancellationToken.None);

        _mockRunStore.Verify(s => s.DeleteRunAsync(runId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteRunAsync_WhenStoreThrows_LogsAndSwallowsException()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        _mockRunStore.Setup(s => s.DeleteRunAsync(runId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk error"));

        var sut = CreateSut();
        // Must not throw — the method swallows the exception and logs a warning
        var act = () => sut.DeleteRunAsync(runId, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeletePersistedRunAsync_WhenStoreThrows_LogsAndSwallowsException()
    {
        var runId = Guid.NewGuid().ToString();
        _mockRunStore.Setup(s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk error"));

        var sut = CreateSut();
        // Must not throw — the method swallows the exception and logs a warning
        var act = () => sut.DeletePersistedRunAsync(runId);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateRunAsync_WhenStoreThrows_LogsAndSwallowsException()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        var run = new ConsolidationRun
        {
            RunId = runId.Value,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
        _mockRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        _mockRunStore.Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk error"));

        var sut = CreateSut();
        // Must not throw — the catch block swallows and logs
        var act = () => sut.UpdateRunAsync(runId, ConsolidationRunStatus.Succeeded, "done", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TransitionToRunningAsync_WhenStoreThrows_LogsAndSwallowsException()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        _mockRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk error"));

        var sut = CreateSut();
        var act = () => sut.TransitionToRunningAsync(runId, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TransitionToRunningAsync_WhenRunNotFound_DoesNotThrow()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        _mockRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        var sut = CreateSut();
        var act = () => sut.TransitionToRunningAsync(runId, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveHarnessSuggestionsAsync_WhenStoreThrows_LogsAndSwallowsException()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 1,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.9m,
            Suggestions = new List<HarnessSuggestion>()
        };
        _mockHarnessStore.Setup(s => s.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk error"));

        var sut = CreateSut();
        var act = () => sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Issue #3027 — DB-layer dedup (replaced in-memory _runningRuns) ────────

    /// <summary>
    /// After issue #3027, dedup is enforced by the DB-layer partial unique index via the
    /// API (KubernetesWorkDistributor maps 409 → DistributionResult(Success=true, WorkItemId=null)).
    /// CleanupOrphanedRunsAsync no longer populates an in-memory dictionary.
    /// A second trigger after startup only succeeds or fails based on what DistributeAsync returns.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_WithPendingRun_DoesNotBlockSubsequentTrigger_DbLayerDedup()
    {
        // Arrange: simulate a Pending run existing from a previous session
        var pendingRun = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { pendingRun });

        // The distributor returns success on the first call — the DB's unique index would enforce
        // dedup in production, but here we're testing that CleanupOrphanedRunsAsync itself
        // no longer blocks the trigger (i.e., no in-memory key is inserted).
        var sut = CreateSut();
        await sut.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // Act: trigger for the same (type, templateId) — should go through to DistributeAsync
        // (DB layer is the guard; unit test distributor mock returns success)
        var triggerResult = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);

        // Assert: CleanupOrphanedRunsAsync did NOT insert an in-memory dedup key.
        // The trigger reaches DistributeAsync and returns success (WorkItemId="wi-delegation-test").
        triggerResult.Should().NotBeNull(
            because: "_runningRuns was removed in #3027; CleanupOrphanedRunsAsync no longer " +
                     "blocks TriggerAsync — DB-layer dedup via partial unique index is the guard");
        // TODO [WARNING]: Only a non-null return is asserted. A regression that short-circuits
        // TriggerAsync before persisting (e.g. returns a recycled run object) would still pass.
        // Add _mockRunStore.Verify(s => s.SaveRunAsync(...), Times.AtLeastOnce) or a
        // distributor-verify on the IssueIdentifier to give this test stronger coverage.
        // (review-findings-testqualityreviewer.md)
    }

    /// <summary>
    /// A Pending run for one (type, templateId) pair must not block TriggerAsync for a
    /// different (type, templateId) pair. This remains true after _runningRuns removal.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_PendingForOneType_DoesNotBlockTriggerForDifferentType()
    {
        var pendingRun = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { pendingRun });

        var sut = CreateSut();
        await sut.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // TriggerAsync for a different type with the same templateId must succeed
        var differentTypeRun = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection,
            new TemplateId("t1"),
            CancellationToken.None);
        differentTypeRun.Should().NotBeNull(
            because: "a Pending BrainConsolidation run must not block RefactoringDetection for the same template");
        _mockRunStore.Verify(s => s.SaveRunAsync(
            It.Is<ConsolidationRun>(r => r.Type == ConsolidationRunType.RefactoringDetection),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "the RefactoringDetection run must be persisted to the store");
    }
}
