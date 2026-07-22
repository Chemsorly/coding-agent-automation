using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Edge case tests for the persistence abstraction that guard against data loss,
/// corruption resilience, and serialization fidelity.
/// </summary>
public sealed class PersistenceEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public PersistenceEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"edge-case-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ── LoopState null field handling ───────────────────────────────────

    /// <summary>
    /// LoopState with null StartedAt/StoppedAt must round-trip correctly.
    /// </summary>
    [Fact]
    public async Task LoopStateStore_HandlesNullDateTimeOffsetFields()
    {
        var store = new FileSystemLoopStateStore(Path.Combine(_tempDir, "loop.json"));

        await store.WriteAsync(new LoopState { IsActive = false, StartedAt = null, StoppedAt = null }, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        loaded.StartedAt.Should().BeNull();
        loaded.StoppedAt.Should().BeNull();
    }

    /// <summary>
    /// LoopState with populated dates must round-trip.
    /// </summary>
    [Fact]
    public async Task LoopStateStore_PreservesDateTimeOffsetPrecision()
    {
        var store = new FileSystemLoopStateStore(Path.Combine(_tempDir, "loop.json"));
        var now = DateTimeOffset.UtcNow;

        await store.WriteAsync(new LoopState { IsActive = true, StartedAt = now }, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.StartedAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
    }

    // ── Corrupt file resilience ─────────────────────────────────────────

    /// <summary>
    /// LoadAllRunsAsync skips corrupt JSON files and returns the valid ones.
    /// A single corrupt file must not take down the entire history.
    /// </summary>
    [Fact]
    public async Task ConsolidationRunStore_LoadAll_SkipsCorruptFiles_ReturnsValid()
    {
        var runsDir = Path.Combine(_tempDir, "runs");
        var store = new FileSystemConsolidationRunStore(runsDir);

        // Write one valid run
        var validRun = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Succeeded
        };
        await store.SaveRunAsync(validRun, CancellationToken.None);

        // Write a corrupt file directly
        await File.WriteAllTextAsync(
            Path.Combine(runsDir, $"{Guid.NewGuid()}.json"),
            "{{{{ not valid JSON at all !!!!!");

        // Write an empty file
        await File.WriteAllTextAsync(
            Path.Combine(runsDir, $"{Guid.NewGuid()}.json"), "");

        var all = await store.LoadAllRunsAsync(CancellationToken.None);

        // Only the valid run should be returned
        all.Should().ContainSingle();
        all[0].RunId.Should().Be(validRun.RunId);
    }

    // ── Concurrency guard still works ───────────────────────────────────

    /// <summary>
    /// Two concurrent TriggerAsync for the same type+template — second must be rejected.
    /// Ensures the ConcurrentDictionary guard still works after constructor refactoring.
    /// </summary>
    [Fact]
    public async Task ConsolidationService_ConcurrencyGuard_RejectsDuplicateTrigger()
    {
        var store = new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs"));
        var harnessStore = new FileSystemHarnessSuggestionStore(Path.Combine(_tempDir, "h.json"));
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "D", TemplateIds = new List<string> { "t1" } }
            });
        mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T", IssueProviderId = "i", RepoProviderId = "r", Enabled = true }
            });
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>());

        var sut = new ConsolidationService(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            mockProjectStore.Object,
            mockHistory.Object,
            store,
            harnessStore);

        var first = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);
        var second = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull(); // rejected by concurrency guard
    }

    // ── GetLastSuccessfulHarnessRunTimestampAsync ────────────────────────

    /// <summary>
    /// With no runs in the store, returns DateTimeOffset.MinValue (not crash).
    /// </summary>
    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestamp_EmptyStore_ReturnsMinValue()
    {
        var store = new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs"));
        var harnessStore = new FileSystemHarnessSuggestionStore(Path.Combine(_tempDir, "h.json"));
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());
        mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>());

        var sut = new ConsolidationService(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            mockProjectStore.Object,
            mockHistory.Object,
            store,
            harnessStore);

        var result = await sut.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        result.Should().Be(DateTimeOffset.MinValue);
    }
}
