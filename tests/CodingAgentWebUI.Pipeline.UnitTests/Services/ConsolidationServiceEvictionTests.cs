using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the stale-entry eviction path in <see cref="ConsolidationService.TriggerAsync"/>.
///
/// Scenario: a run completes (Succeeded/Failed/Cancelled) in the store via the API, but
/// ConsolidationService never received the <see cref="IConsolidationService.UpdateRunAsync"/>
/// call (multi-process gap). The in-memory <c>_runningRuns</c> dict still holds the old entry.
/// When a new trigger arrives for the same (type, templateId), TryEvictAndRetryAsync should
/// detect the stale entry and allow the new run through.
/// </summary>
public sealed class ConsolidationServiceEvictionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runsDir;
    private readonly ILogger _logger;
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<IConsolidationRunStore> _mockRunStore;
    private readonly PipelineConfiguration _config;

    private const string TemplateId = "tmpl-1";
    private const ConsolidationRunType RunType = ConsolidationRunType.BrainConsolidation;

    public ConsolidationServiceEvictionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"eviction-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _runsDir = Path.Combine(_tempDir, "runs");

        _logger = new LoggerConfiguration().CreateLogger();

        _mockRunHistory = new Mock<IPipelineRunHistoryService>();
        _mockRunHistory
            .Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>());

        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore
            .Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { TemplateId }
                }
            });
        _mockProjectStore
            .Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new()
                {
                    Id = TemplateId,
                    Name = "DotNet Repo",
                    IssueProviderId = "ip-1",
                    RepoProviderId = "rp-1",
                    BrainProviderId = "bp-1",
                    Enabled = true
                }
            });

        _mockRunStore = new Mock<IConsolidationRunStore>();
        // Default: SaveRunAsync and DeleteRunAsync are no-ops
        _mockRunStore
            .Setup(x => x.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRunStore
            .Setup(x => x.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRunStore
            .Setup(x => x.LoadAllRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>());

        _config = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private ConsolidationService CreateSut() => new(new ConsolidationServiceDependencies(
        _logger,
        _config,
        _mockProjectStore.Object,
        _mockRunHistory.Object,
        _mockRunStore.Object,
        new Mock<IHarnessSuggestionStore>().Object,
        WorkspaceManager: new ConsolidationWorkspaceManager(_logger, _config)));

    /// <summary>
    /// Seeds the first run into _runningRuns by triggering it normally, then configures
    /// the store mock to return <paramref name="storeStatus"/> when the eviction check queries it.
    /// Returns the SUT (with first run already in _runningRuns) and the first run's RunId.
    /// </summary>
    private async Task<(ConsolidationService sut, string firstRunId)> SeedStaleEntryAsync(
        ConsolidationRunStatus storeStatus)
    {
        var sut = CreateSut();

        // First trigger — seeds _runningRuns[(RunType, TemplateId)]
        var firstRun = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);
        firstRun.Should().NotBeNull("prerequisite: first run must succeed to seed _runningRuns");

        // Make GetByIdAsync return the first run with the given status to simulate
        // the store being updated externally (e.g., via API) without notifying ConsolidationService.
        var staleStoredRun = new ConsolidationRun
        {
            RunId = firstRun!.RunId,
            Type = RunType,
            TemplateId = TemplateId,
            TemplateName = "DotNet Repo",
            Status = storeStatus,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        _mockRunStore
            .Setup(x => x.GetByIdAsync(new RunId(firstRun.RunId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleStoredRun);

        return (sut, firstRun.RunId);
    }

    #region Eviction succeeds for each terminal status

    [Fact]
    public async Task TriggerAsync_StaleEntrySucceeded_Evicts_AllowsNewRun()
    {
        // Arrange: first run is Succeeded in store but still in _runningRuns
        var (sut, _) = await SeedStaleEntryAsync(ConsolidationRunStatus.Succeeded);

        // Act: second trigger for same (type, templateId)
        var second = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);

        // Assert: eviction path allowed the new run through
        second.Should().NotBeNull(
            "stale Succeeded entry must be evicted so the new run is accepted");
        second!.Status.Should().Be(ConsolidationRunStatus.Running);
    }

    [Fact]
    public async Task TriggerAsync_StaleEntryFailed_Evicts_AllowsNewRun()
    {
        // Arrange: first run is Failed in store but still in _runningRuns
        var (sut, _) = await SeedStaleEntryAsync(ConsolidationRunStatus.Failed);

        // Act
        var second = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);

        // Assert
        second.Should().NotBeNull(
            "stale Failed entry must be evicted so the new run is accepted");
    }

    [Fact]
    public async Task TriggerAsync_StaleEntryCancelled_Evicts_AllowsNewRun()
    {
        // Arrange: first run is Cancelled in store but still in _runningRuns
        var (sut, _) = await SeedStaleEntryAsync(ConsolidationRunStatus.Cancelled);

        // Act
        var second = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);

        // Assert
        second.Should().NotBeNull(
            "stale Cancelled entry must be evicted so the new run is accepted");
    }

    #endregion

    #region Eviction blocked — not terminal

    [Fact]
    public async Task TriggerAsync_StaleEntryStillRunningInStore_NotEvicted_ReturnsNull()
    {
        // Arrange: entry is in _runningRuns AND the store also shows Running — legitimate duplicate
        var (sut, _) = await SeedStaleEntryAsync(ConsolidationRunStatus.Running);

        // Act
        var second = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);

        // Assert: not evicted → duplicate rejection
        second.Should().BeNull(
            "a genuinely running entry (Running status in store) must not be evicted");
    }

    [Fact]
    public async Task TriggerAsync_StaleEntryStillQueuedInStore_NotEvicted_ReturnsNull()
    {
        // Arrange: entry is Queued in store — also not terminal, must not be evicted
        var (sut, _) = await SeedStaleEntryAsync(ConsolidationRunStatus.Queued);

        // Act
        var second = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);

        // Assert
        second.Should().BeNull(
            "a Queued entry (not terminal) must not be evicted");
    }

    #endregion

    #region Eviction path — store returns null

    [Fact]
    public async Task TriggerAsync_StaleEntry_StoreReturnsNull_NotEvicted_ReturnsNull()
    {
        // Arrange: first run seeds _runningRuns; store returns null (run not found)
        var sut = CreateSut();
        var firstRun = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);
        firstRun.Should().NotBeNull();

        _mockRunStore
            .Setup(x => x.GetByIdAsync(new RunId(firstRun!.RunId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        // Act
        var second = await sut.TriggerAsync(RunType, TemplateId, CancellationToken.None);

        // Assert: GetByIdAsync returned null → stored is null → eviction skipped → rejected
        second.Should().BeNull(
            "when the store returns null for the existing run, eviction must not proceed");
    }

    #endregion
}
