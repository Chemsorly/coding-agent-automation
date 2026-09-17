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

    private ConsolidationService CreateSut() => new(new ConsolidationServiceDependencies(
        new LoggerConfiguration().CreateLogger(),
        new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
        _mockProjectStore.Object,
        _mockRunHistory.Object,
        _mockRunStore.Object,
        _mockHarnessStore.Object,
        new Mock<IProviderConfigStore>().Object,
        WorkspaceManager: new ConsolidationWorkspaceManager(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() })));

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
            Status = ConsolidationRunStatus.Queued
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
            Status = ConsolidationRunStatus.Queued
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
        _mockHarnessStore.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var result = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        result.Should().BeSameAs(expected);
        _mockHarnessStore.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task RehydrateQueuedRunsAsync_WithQueuedRuns_ReturnsThemAndAddsToRunningRuns()
    {
        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Queued
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { run });

        var sut = CreateSut();
        var result = await sut.RehydrateQueuedRunsAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].RunId.Should().Be(runId);
    }

    [Fact]
    public async Task RehydrateQueuedRunsAsync_WithNoQueuedRuns_ReturnsEmpty()
    {
        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running // Not queued
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { run });

        var sut = CreateSut();
        var result = await sut.RehydrateQueuedRunsAsync(CancellationToken.None);
        result.Should().BeEmpty();
    }

    /// <summary>
    /// Regression test for issue #2584: a Pending run (successfully submitted to the unified
    /// WorkItem queue) must NOT be returned by RehydrateQueuedRunsAsync. If it were returned,
    /// the retry background service would re-dispatch it every sweep, hitting the existing
    /// Pending WorkItem and getting an idempotent 409 each time — a no-op but incorrect loop.
    /// </summary>
    [Fact]
    public async Task RehydrateQueuedRunsAsync_ExcludesPendingRuns()
    {
        var pendingRun = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending // Already submitted to unified queue
        };
        var queuedRun = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "t1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Queued // Genuinely needs retry
        };
        _mockRunStore.Setup(s => s.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { pendingRun, queuedRun });

        var sut = CreateSut();
        var result = await sut.RehydrateQueuedRunsAsync(CancellationToken.None);

        // Only the Queued run must be returned — Pending must be excluded.
        result.Should().HaveCount(1,
            because: "Pending runs have a live WorkItem in the queue and must not be retried");
        result[0].RunId.Should().Be(queuedRun.RunId);
        result[0].Status.Should().Be(ConsolidationRunStatus.Queued);
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
    /// run must be re-added to _runningRuns so that a subsequent TriggerAsync call for the same
    /// (type, templateId) key is rejected as a duplicate (returns null).
    ///
    /// Before the fix, RehydrateQueuedRunsAsync silently skipped Pending runs, leaving the key
    /// absent from _runningRuns. TriggerAsync's TryAdd would then succeed and create a second
    /// ConsolidationRun + WorkItem for the same template — a genuine duplicate consolidation.
    /// </summary>
    [Fact]
    public async Task RehydrateQueuedRunsAsync_WithPendingRun_AddsKeyToRunningRunsForDedup()
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

        // Act: rehydrate (simulates first sweep of ConsolidationRetryBackgroundService after restart)
        var result = await sut.RehydrateQueuedRunsAsync(CancellationToken.None);

        // Assert 1: Pending runs must NOT be returned for dispatch
        result.Should().BeEmpty(
            because: "Pending runs already have a live WorkItem and must not be re-dispatched");

        // Assert 2: _runningRuns must contain the key so TriggerAsync rejects a duplicate
        // Observable indirectly: TriggerAsync for the same (type, templateId) must return null
        var duplicate = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);
        duplicate.Should().BeNull(
            because: "the Pending run's key should have been re-added to _runningRuns, blocking a duplicate trigger");
    }

    /// <summary>
    /// A Pending run for one (type, templateId) pair must not block TriggerAsync for a
    /// different (type, templateId) pair. The dedup key is (type, templateId) — not just templateId.
    /// </summary>
    [Fact]
    public async Task RehydrateQueuedRunsAsync_PendingForOneType_DoesNotBlockTriggerForDifferentType()
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
        var result = await sut.RehydrateQueuedRunsAsync(CancellationToken.None);

        // Pending run excluded from dispatch list
        result.Should().BeEmpty();

        // TriggerAsync for a different type with the same templateId must succeed
        // (different key = (RefactoringDetection, "t1") vs (BrainConsolidation, "t1"))
        var differentTypeRun = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection,
            new TemplateId("t1"),
            CancellationToken.None);
        differentTypeRun.Should().NotBeNull(
            because: "a Pending BrainConsolidation run must not block RefactoringDetection for the same template");
        // TODO [WARNING]: This assertion only confirms the in-memory TryAdd succeeded. It does not
        // verify that SaveRunAsync was called on the store, so a regression that accidentally removes
        // PersistRunAsync from the TriggerAsync success path would not be caught. Add:
        // _mockRunStore.Verify(s => s.SaveRunAsync(It.Is<ConsolidationRun>(r => r.Type == ConsolidationRunType.RefactoringDetection), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Multiple Pending runs (each with a distinct (type, templateId) key) must all be
    /// re-added to _runningRuns so that subsequent TriggerAsync calls for each key are rejected.
    /// </summary>
    [Fact]
    public async Task RehydrateQueuedRunsAsync_MultiplePendingRuns_AllKeysAddedToRunningRuns()
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
        var result = await sut.RehydrateQueuedRunsAsync(CancellationToken.None);

        result.Should().BeEmpty(because: "neither Pending run should be returned for dispatch");

        // Both (type, templateId) keys must be in _runningRuns — verified via TriggerAsync
        var dup1 = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);
        var dup2 = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection,
            new TemplateId("t1"),
            CancellationToken.None);

        dup1.Should().BeNull(because: "BrainConsolidation/t1 key must be in _runningRuns after rehydration");
        dup2.Should().BeNull(because: "RefactoringDetection/t1 key must be in _runningRuns after rehydration");
        // TODO [WARNING]: This test only validates the "both keys are blocked" half of the scenario.
        // It does not call TriggerAsync for a *third distinct key* (e.g., BrainConsolidation/"t2")
        // to confirm normal dispatch still works when multiple Pending keys are present. A regression
        // that accidentally blocked all TriggerAsync calls (e.g., a key comparison bug) would not be
        // caught here. Add a TriggerAsync call for an unblocked (type, templateId) pair and assert
        // the result is non-null to close this gap.
    }

    /// <summary>
    /// When a Pending run has been completed (terminal status in the store) between the restart
    /// and a subsequent TriggerAsync call, the stale _runningRuns entry must be evicted by
    /// TryEvictAndRetryAsync so the new trigger succeeds.
    ///
    /// Key subtlety: _runningRuns holds the run object with Status=Pending (the snapshot from
    /// rehydration time). TryEvictAndRetryAsync evicts by loading the run from the STORE via
    /// GetByIdAsync and checking IsTerminalStatus(stored.Status) — it does NOT use the in-memory
    /// status. The mock must therefore return Status=Succeeded from GetByIdAsync; if it returned
    /// Status=Pending, eviction would not fire (correctly — the run is still in-flight).
    /// </summary>
    [Fact]
    public async Task RehydrateQueuedRunsAsync_ThenTriggerAsync_PendingRunCompletedInStore_AllowsNewTrigger()
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

        // Rehydrate: inserts (BrainConsolidation, "t1") key into _runningRuns with Pending snapshot
        await sut.RehydrateQueuedRunsAsync(CancellationToken.None);

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
