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
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new List<string> { "t1" } }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "Template", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true }
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
    public async Task CancelQueuedRunAsync_Calls_GetByIdAsync_OnStore()
    {
        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.RefactoringDetection,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        _mockRunStore.Setup(s => s.GetByIdAsync((RunId)runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);

        var sut = CreateSut();
        var result = await sut.CancelQueuedRunAsync(runId, CancellationToken.None);

        result.Should().BeTrue();
        _mockRunStore.Verify(s => s.GetByIdAsync((RunId)runId, It.IsAny<CancellationToken>()), Times.Once);
        _mockRunStore.Verify(s => s.SaveRunAsync(It.Is<ConsolidationRun>(r =>
            r.Status == ConsolidationRunStatus.Cancelled), It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task CancelQueuedRunAsync_WhenStoreThrows_ReturnsFalse()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        _mockRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk error"));

        var sut = CreateSut();
        var result = await sut.CancelQueuedRunAsync(runId, CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CancelQueuedRunAsync_WhenRunNotFound_ReturnsFalse()
    {
        var runId = new RunId(Guid.NewGuid().ToString());
        _mockRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        var sut = CreateSut();
        var result = await sut.CancelQueuedRunAsync(runId, CancellationToken.None);
        result.Should().BeFalse();
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

    // ── Issue #2619 — Pending runs rehydration into _runningRuns ──────────────

    /// <summary>
    /// Regression test for issue #2619: after an orchestrator restart, a Pending consolidation
    /// run must be re-added to _runningRuns by CleanupOrphanedRunsAsync so that a subsequent
    /// TriggerAsync call for the same (type, templateId) key is rejected as a duplicate.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_WithPendingRun_AddsKeyToRunningRunsForDedup()
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

        // Act: startup cleanup adds Pending run keys back into _runningRuns for dedup
        await sut.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // Assert: _runningRuns must contain the key so TriggerAsync rejects a duplicate
        var duplicate = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);
        duplicate.Should().BeNull(
            because: "the Pending run's key should have been re-added to _runningRuns by CleanupOrphanedRunsAsync");
    }

    /// <summary>
    /// A Pending run for one (type, templateId) pair must not block TriggerAsync for a
    /// different (type, templateId) pair.
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
        // TODO [WARNING]: This test does not verify that SaveRunAsync was called for the RefactoringDetection
        // run, only that TriggerAsync returned non-null. A regression that removes PersistRunAsync from
        // the success path would pass this assertion. Add:
        //   _mockRunStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()), Times.Once)
        // to close this gap. (review-findings-testqualityreviewer.md)
    }

    /// <summary>
    /// Multiple Pending runs must all have their keys added to _runningRuns so subsequent
    /// TriggerAsync calls for each key are rejected.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_MultiplePendingRuns_AllKeysAddedToRunningRuns()
    {
        var pending1 = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        var pending2 = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { pending1, pending2 });

        var sut = CreateSut();
        await sut.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // Both (type, templateId) keys must be in _runningRuns — verified via TriggerAsync
        var dup1 = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);
        var dup2 = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection,
            new TemplateId("t1"),
            CancellationToken.None);

        dup1.Should().BeNull(because: "BrainConsolidation/t1 key must be in _runningRuns after cleanup");
        dup2.Should().BeNull(because: "RefactoringDetection/t1 key must be in _runningRuns after cleanup");
        // TODO [WARNING]: This test only verifies that both keys are blocked (both TriggerAsync calls
        // return null). It does not verify that an unrelated (type, templateId) pair is NOT blocked.
        // A key-comparison bug that blocked all TriggerAsync calls regardless of key would not be caught.
        // Consider adding a third TriggerAsync call for e.g. BrainConsolidation/"t2" and asserting it
        // returns non-null to confirm the dedup is key-scoped. (review-findings-testqualityreviewer.md)
    }


    [Fact]
    public async Task CleanupOrphanedRunsAsync_ThenTriggerAsync_PendingRunCompletedInStore_AllowsNewTrigger()
    {
        var runId = Guid.NewGuid().ToString();
        var pendingRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { pendingRun });

        var sut = CreateSut();

        // Simulate startup: CleanupOrphanedRunsAsync inserts (BrainConsolidation, "t1") key
        // into _runningRuns for the Pending run.
        await sut.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // Simulate Scheduler completing the run between restart and next trigger:
        // GetByIdAsync now returns Status=Succeeded (authoritative store value).
        // Note: the in-memory _runningRuns entry still has Status=Pending — TryEvictAndRetryAsync
        // uses the STORE status, not the in-memory status, to decide whether to evict.
        var succeededRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = pendingRun.StartedAtUtc,
            Status = ConsolidationRunStatus.Succeeded
        };
        _mockRunStore.Setup(s => s.GetByIdAsync(
                It.Is<RunId>(r => r.Value == runId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(succeededRun);

        // Act: TriggerAsync should evict the stale Pending entry and allow the new run
        var newRun = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);

        newRun.Should().NotBeNull(
            because: "TryEvictAndRetryAsync must evict the stale Pending entry when the store shows Succeeded");
        newRun!.RunId.Should().NotBe(runId,
            because: "the new run must have a fresh RunId, not the completed run's ID");
    }
}
