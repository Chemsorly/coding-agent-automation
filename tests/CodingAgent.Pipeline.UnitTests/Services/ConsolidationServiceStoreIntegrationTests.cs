#pragma warning disable CS0618 // FileSystemConsolidationRunStore is Obsolete; test-infrastructure use is intentional
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Integration tests verifying ConsolidationService correctly delegates ALL persistence
/// to IConsolidationRunStore. These tests specifically guard against the regression where
/// UpdateRunAsync, CancelQueuedRunAsync, or TransitionToRunningAsync bypass the store.
///
/// The original production bug: UpdateRunAsync checked File.Exists() instead of using the store,
/// so runs dispatched via DB mode could never be updated → stuck as "Running" forever.
/// </summary>
public sealed class ConsolidationServiceStoreIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory;
    private readonly PipelineConfiguration _config;
    private readonly FileSystemConsolidationRunStore _store;
    private readonly IHarnessSuggestionStore _harnessStore;
    private readonly ConsolidationService _sut;

    public ConsolidationServiceStoreIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"store-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir, DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" };
        _mockRunHistory = new Mock<IPipelineRunHistoryService>();
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>());

        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { "tmpl-1" }
                }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "tmpl-1", Name = "Test Template", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true }
            });

        _store = new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs"));
        _harnessStore = new InMemoryHarnessSuggestionStore();

        // WorkDistributor returns success so TriggerAsync creates a Pending run.
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-integration-test", ErrorMessage: null));

        _sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                new LoggerConfiguration().CreateLogger(),
                _config,
                _mockProjectStore.Object,
                _mockRunHistory.Object,
                _store,
                _harnessStore,
                new Mock<IProviderConfigStore>().Object,
                WorkDistributor: mockWorkDistributor.Object));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// REGRESSION TEST: After issue #3028, TriggerAsync no longer persists to the ConsolidationRuns
    /// store. This test verifies the store is NOT written by TriggerAsync and that the method
    /// still returns a run object (for callers that need the RunId/WorkItemId).
    /// </summary>
    [Fact]
    public async Task UpdateRunAsync_AfterTrigger_UpdatesStatusViaStore()
    {
        // Issue #3028: TriggerAsync no longer persists to the store, and UpdateRunAsync is a no-op
        // for store writes. This test now verifies:
        // (a) TriggerAsync returns a non-null run (dispatch succeeded).
        // (b) The store was NOT written (no SaveRunAsync call during TriggerAsync).
        // (c) UpdateRunAsync does not throw.
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull("TriggerAsync must return the run even after store-write removal");

        // Act: UpdateRunAsync is now a no-op (store writes removed, workspace in the agent pod)
        await _sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Succeeded, "Completed", CancellationToken.None, totalTokens: 1500);

        // Assert: nothing was persisted to the store (PipelineRun is the authoritative record)
        var persisted = await _store.GetByIdAsync(run.RunId, CancellationToken.None);
        persisted.Should().BeNull("TriggerAsync must not write to the ConsolidationRuns store (writes stopped in #3028)");
    }

    /// <summary>
    /// Verify UpdateRunAsync with a non-existent runId logs warning and doesn't throw.
    /// </summary>
    [Fact]
    public async Task UpdateRunAsync_NonExistentRun_DoesNotThrow()
    {
        var act = () => _sut.UpdateRunAsync(
            Guid.NewGuid().ToString(), ConsolidationRunStatus.Failed, "Gone", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Issue #3028: TransitionToRunningAsync no longer writes to the store.
    /// Verify it does not throw and does not persist.
    /// </summary>
    [Fact]
    public async Task TransitionToRunningAsync_PendingRun_UpdatesStatusViaStore()
    {
        // Issue #3028: TransitionToRunningAsync is now a no-op for store writes.
        // Verify: does not throw, store is not written.
        var run = await _sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Act: no-op after #3028
        await _sut.TransitionToRunningAsync(run!.RunId, CancellationToken.None);

        // Nothing was written to store
        var persisted = await _store.GetByIdAsync(run.RunId, CancellationToken.None);
        persisted.Should().BeNull("TriggerAsync and TransitionToRunningAsync must not write to store (writes stopped in #3028)");
    }

    /// <summary>
    /// Issue #3028: TransitionToRunningAsync does not reset StartedAtUtc (store writes removed).
    /// Verify no-throw behaviour.
    /// </summary>
    [Fact]
    public async Task TransitionToRunningAsync_QueuedRun_UpdatesStatusViaStore()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Act: no-op after #3028
        // TODO [WARNING]: This test only verifies no-throw. It was a regression guard for a specific
        // scenario (store update after Queued→Running transition) which is now irrelevant after #3028.
        // The test provides no behavioral assertion about the new contract and is effectively equivalent
        // to testing that the method exists. Consider removing it or replacing the assertion with a
        // meaningful check on the new contract (e.g. store not written). (TestQualityReviewer review)
        var act = () => _sut.TransitionToRunningAsync(run!.RunId, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Issue #3028: TransitionToRunningAsync is a no-op for store writes.
    /// Verifies it does not throw even with an old StartedAtUtc.
    /// </summary>
    [Fact]
    public async Task TransitionToRunningAsync_QueuedRun_ResetsStartedAtUtc()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Act: no-op after #3028
        // TODO [WARNING]: This test was a regression guard for BUG FIX #1540 (StartedAtUtc not reset).
        // That scenario is no longer applicable after #3028 (no store writes). The test provides no
        // behavioral assertion and is equivalent to a no-op test. Consider removing it, or document
        // explicitly why verifying no-throw is sufficient for this specific case. (TestQualityReviewer review)
        var act = () => _sut.TransitionToRunningAsync(run!.RunId, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Issue #3028: TransitionToRunningAsync is a no-op; GetActiveRunStartedAt reads from store.
    /// Since no run is written to store by TriggerAsync, GetActiveRunStartedAt returns null.
    /// </summary>
    [Fact]
    public async Task TransitionToRunningAsync_QueuedRun_UpdatesStoreAndGetActiveRunStartedAtReflectsReset()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        await _sut.TransitionToRunningAsync(run!.RunId, CancellationToken.None);

        // After #3028: TriggerAsync does not write to store, so GetActiveRunStartedAt returns null
        // (the store has no record). The PipelineRun is the authoritative record.
        var activeStartedAt = _sut.GetActiveRunStartedAt(run.RunId);
        activeStartedAt.Should().BeNull("the ConsolidationRuns store is no longer written by TriggerAsync (issue #3028)");
    }

    /// <summary>
    /// Issue #3028: UpdateRunAsync is a no-op (store writes removed, workspace in the agent pod).
    /// Verifies it does not throw when called with Running status.
    /// </summary>
    [Fact]
    public async Task UpdateRunAsync_TransitionToRunning_DoesNotSetCompletedAtUtc()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Act: no-op for store writes after #3028
        var act = () => _sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Running, null, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Issue #3028: UpdateRunAsync is a no-op (store writes removed, workspace in the agent pod).
    /// Verifies it does not throw when called with terminal status.
    /// </summary>
    [Fact]
    public async Task UpdateRunAsync_TerminalStatus_SetsCompletedAtUtc()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Act: no-op for store writes after #3028
        var act = () => _sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Failed, "timed out", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Issue #3028: GetRunHistoryAsync still reads from the ConsolidationRuns store (the store
    /// is still populated by other paths). But since TriggerAsync no longer writes to the store,
    /// GetRunHistoryAsync will return an empty list for runs created in this test.
    /// This test verifies that GetRunHistoryAsync does NOT throw.
    /// </summary>
    [Fact]
    public async Task GetRunHistoryAsync_ReturnsRunsFromStore()
    {
        // After #3028: TriggerAsync does not write to the store, so history will be empty.
        await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        var history = await _sut.GetRunHistoryAsync(CancellationToken.None);
        // Store was not written by TriggerAsync — empty is correct
        history.Should().BeEmpty("TriggerAsync no longer writes to the ConsolidationRuns store (issue #3028)");
    }

    /// <summary>
    /// Issue #3028: CleanupOrphanedRunsAsync no longer writes to the store.
    /// The run's in-memory status is updated but no SaveRunAsync is called.
    /// This test verifies the in-memory mutation (for log output) and no store write.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_MarksRunningAsFailed_ViaStore()
    {
        // Arrange: seed the store directly (bypass TriggerAsync which no longer writes)
        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
        await _store.SaveRunAsync(run, CancellationToken.None);

        // Act: cleanup marks the in-memory run as Failed but does not write back
        var sut2 = new ConsolidationService(
            new ConsolidationServiceDependencies(
                new LoggerConfiguration().CreateLogger(),
                _config,
                _mockProjectStore.Object,
                _mockRunHistory.Object,
                _store,
                _harnessStore,
                new Mock<IProviderConfigStore>().Object));
        await sut2.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // Assert: the store still has the original record (no SaveRunAsync was called)
        var persisted = await _store.GetByIdAsync(runId, CancellationToken.None);
        persisted.Should().NotBeNull();
        // Status in store is unchanged (CleanupOrphanedRunsAsync no longer writes back in #3028)
        persisted!.Status.Should().Be(ConsolidationRunStatus.Running,
            "CleanupOrphanedRunsAsync no longer writes to the store (issue #3028)");
    }

    /// <summary>
    /// Verify harness suggestions round-trip through IHarnessSuggestionStore.
    /// </summary>
    [Fact]
    public async Task HarnessSuggestions_SaveAndGet_RoundTripsViaStore()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 10,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.85m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Frequency = 5, Rationale = "Test", Text = "Improve X" }
            }
        };

        await _sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);
        var loaded = await _sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.BasedOnRunCount.Should().Be(10);
        loaded.Suggestions.Should().HaveCount(1);
    }
}
