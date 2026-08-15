using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;
using System.Text.Json;

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

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            mockProjectStore.Object,
            mockHistory.Object,
            store,
            harnessStore));

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
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>());

        var feedbackCache = new ConsolidationFeedbackCache(
            new LoggerConfiguration().CreateLogger(),
            store,
            mockHistory.Object);

        var result = await feedbackCache.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        result.Should().Be(DateTimeOffset.MinValue);
    }

    // ── PipelineRunSummary backward-compat deserialization ──────────────

    /// <summary>
    /// A PipelineRunSummary JSON payload that was written before CacheReadTokens/CacheWriteTokens
    /// existed (i.e. those fields are absent) must deserialize cleanly with both fields = 0.
    /// This validates the backward-compat acceptance criterion for file-based and Postgres JSONB paths.
    /// </summary>
    [Fact]
    public void PipelineRunSummary_OldSummaryWithoutCacheFields_DeserializesCleanlyWithZero()
    {
        // Arrange: JSON that was written before CacheReadTokens/CacheWriteTokens existed.
        // The fields are intentionally absent (not "cacheReadTokens": 0) to simulate old records.
        const string oldJson = """
            {
              "runId": "test-run-old",
              "issueIdentifier": "42",
              "issueTitle": "Old Run",
              "finalStep": "Completed",
              "startedAt": "2026-01-01T00:00:00Z",
              "startedAtOffset": "2026-01-01T00:00:00+00:00",
              "totalTokens": 12345,
              "totalCost": 0.05,
              "initiatedBy": "manual"
            }
            """;

        // Act
        var summary = JsonSerializer.Deserialize<PipelineRunSummary>(oldJson, CodingAgentWebUI.Pipeline.PipelineJsonOptions.Default);

        // Assert
        summary.Should().NotBeNull();
        summary!.RunId.Should().Be("test-run-old");
        summary.TotalTokens.Should().Be(12345);
        summary.CacheReadTokens.Should().Be(0, "missing field in old JSON must default to 0");
        summary.CacheWriteTokens.Should().Be(0, "missing field in old JSON must default to 0");
    }
}
