using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
    private readonly IConsolidationRunStore _store;
    private readonly IHarnessSuggestionStore _harnessStore;
    private readonly ConsolidationService _sut;

    public ConsolidationServiceStoreIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"store-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir };
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
        _harnessStore = new FileSystemHarnessSuggestionStore(Path.Combine(_tempDir, "harness.json"));

        _sut = new ConsolidationService(
            new LoggerConfiguration().CreateLogger(),
            _config,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            _store,
            _harnessStore);
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
    /// REGRESSION TEST: The exact scenario that caused stuck "Running" consolidation runs in production.
    /// TriggerAsync creates and persists a run → UpdateRunAsync must find it via the store and update status.
    /// Before the fix, UpdateRunAsync used File.Exists() and failed silently in DB mode.
    /// </summary>
    [Fact]
    public async Task UpdateRunAsync_AfterTrigger_UpdatesStatusViaStore()
    {
        // Arrange: trigger creates and persists a run
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();
        run!.Status.Should().Be(ConsolidationRunStatus.Running);

        // Act: simulate agent completion callback
        await _sut.UpdateRunAsync(run.RunId, ConsolidationRunStatus.Succeeded, "Completed", CancellationToken.None, totalTokens: 1500);

        // Assert: status persisted in the store
        var persisted = await _store.GetByIdAsync(run.RunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ConsolidationRunStatus.Succeeded);
        persisted.Summary.Should().Be("Completed");
        persisted.TotalTokens.Should().Be(1500);
        persisted.CompletedAtUtc.Should().NotBeNull();
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
    /// REGRESSION TEST: CancelQueuedRunAsync must load the run from the store, not from filesystem directly.
    /// </summary>
    [Fact]
    public async Task CancelQueuedRunAsync_QueuedRun_UpdatesStatusViaStore()
    {
        // Arrange: create a run and manually set it to Queued (simulating dispatch to queue)
        var run = await _sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Manually transition to Queued (simulating what happens when no agent is available)
        run!.Status = ConsolidationRunStatus.Queued;
        await _store.SaveRunAsync(run, CancellationToken.None);

        // Act
        var cancelled = await _sut.CancelQueuedRunAsync(run.RunId, CancellationToken.None);

        // Assert
        cancelled.Should().BeTrue();
        var persisted = await _store.GetByIdAsync(run.RunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ConsolidationRunStatus.Cancelled);
        persisted.Summary.Should().Be("Cancelled by user");
    }

    /// <summary>
    /// REGRESSION TEST: TransitionToRunningAsync must load from store and persist back.
    /// </summary>
    [Fact]
    public async Task TransitionToRunningAsync_QueuedRun_UpdatesStatusViaStore()
    {
        // Arrange: create a run and set to Queued
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();
        run!.Status = ConsolidationRunStatus.Queued;
        await _store.SaveRunAsync(run, CancellationToken.None);

        // Act
        await _sut.TransitionToRunningAsync(run.RunId, CancellationToken.None);

        // Assert
        var persisted = await _store.GetByIdAsync(run.RunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ConsolidationRunStatus.Running);
    }

    /// <summary>
    /// Verify GetRunHistoryAsync returns runs from the store (not from an inline filesystem scan).
    /// </summary>
    [Fact]
    public async Task GetRunHistoryAsync_ReturnsRunsFromStore()
    {
        await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        // Reset internal cache to force re-read from store
        _sut.Reset();

        var sut2 = new ConsolidationService(
            new LoggerConfiguration().CreateLogger(),
            _config,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            _store,
            _harnessStore);

        var history = await sut2.GetRunHistoryAsync(CancellationToken.None);
        history.Should().ContainSingle();
        history[0].Type.Should().Be(ConsolidationRunType.BrainConsolidation);
    }

    /// <summary>
    /// Verify CleanupOrphanedRunsAsync marks Running runs as Failed via the store.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_MarksRunningAsFailed_ViaStore()
    {
        // Arrange: create a run (status = Running)
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Act: simulate restart — new service instance calls cleanup
        var sut2 = new ConsolidationService(
            new LoggerConfiguration().CreateLogger(),
            _config,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            _store,
            _harnessStore);
        await sut2.CleanupOrphanedRunsAsync(CancellationToken.None);

        // Assert
        var persisted = await _store.GetByIdAsync(run!.RunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ConsolidationRunStatus.Failed);
        persisted.Summary.Should().Contain("Orphaned");
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
