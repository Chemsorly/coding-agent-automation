#pragma warning disable CS0618 // FileSystemConsolidationRunStore is Obsolete; test-infrastructure use is intentional
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationFeedbackCache"/>.
/// </summary>
public sealed class ConsolidationFeedbackCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runsDir;
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory;
    private readonly ILogger _logger;

    public ConsolidationFeedbackCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"feedback-cache-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _runsDir = Path.Combine(_tempDir, "runs");

        _mockRunHistory = new Mock<IPipelineRunHistoryService>();
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>());

        _logger = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private ConsolidationFeedbackCache CreateSut() => new(
        _logger,
        new FileSystemConsolidationRunStore(_runsDir),
        _mockRunHistory.Object);

    private static PipelineRunSummary CreateRunSummary(RunFeedback? feedback = null, DateTimeOffset? startedAt = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "test-1",
        IssueTitle = "Test Issue",
        FinalStep = PipelineStep.Completed,
        StartedAt = DateTime.UtcNow.AddMinutes(-30),
        CompletedAt = DateTime.UtcNow,
        StartedAtOffset = startedAt ?? DateTimeOffset.UtcNow,
        Feedback = feedback
    };

    private static ConsolidationRun CreateConsolidationRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.HarnessSuggestions,
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    // TODO: Add test for exception-swallowing behavior in PrepareFeedbackDataAsync — verify that when
    // _runHistoryService.GetRunHistoryAsync or _runStore.LoadAllRunsAsync throws, the exception is
    // caught and logged rather than propagating (regression guard for the try-catch).

    // TODO: Add test for time-based filtering — set up a prior successful harness run at time T with
    // feedback entries both before and after T, and verify only entries after T are included in the
    // serialized JSON (validates the `r.StartedAtOffset > sinceUtc` filter).

    [Fact]
    public async Task PrepareFeedbackDataAsync_WithFeedbackSinceLastRun_StoresJson()
    {
        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback { Category = "test-category" }
        };
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>
            {
                CreateRunSummary(feedback, DateTimeOffset.UtcNow)
            });

        var sut = CreateSut();
        var run = CreateConsolidationRun();

        await sut.PrepareFeedbackDataAsync(run, CancellationToken.None);

        var result = sut.GetFeedbackDataForRun(run.RunId);
        result.Should().NotBeNull();
        result.Should().Contain("test-category");
    }

    [Fact]
    public async Task PrepareFeedbackDataAsync_NoFeedbackSinceLastRun_DoesNotStore()
    {
        var sut = CreateSut();
        var run = CreateConsolidationRun();

        await sut.PrepareFeedbackDataAsync(run, CancellationToken.None);

        var result = sut.GetFeedbackDataForRun(run.RunId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareFeedbackDataAsync_NoPriorSuccessfulRun_UsesMinValue()
    {
        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback { Category = "early" }
        };
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>
            {
                CreateRunSummary(feedback, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
            });

        var sut = CreateSut();
        var run = CreateConsolidationRun();

        await sut.PrepareFeedbackDataAsync(run, CancellationToken.None);

        sut.GetFeedbackDataForRun(run.RunId).Should().NotBeNull();
    }

    [Fact]
    public void GetFeedbackDataForRun_NoPriorPrepare_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.GetFeedbackDataForRun(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClearFeedbackDataForRun_RemovesEntry()
    {
        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback { Category = "to-clear" }
        };
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>
            {
                CreateRunSummary(feedback, DateTimeOffset.UtcNow)
            });

        var sut = CreateSut();
        var run = CreateConsolidationRun();

        await sut.PrepareFeedbackDataAsync(run, CancellationToken.None);
        sut.GetFeedbackDataForRun(run.RunId).Should().NotBeNull();

        sut.ClearFeedbackDataForRun(run.RunId);

        sut.GetFeedbackDataForRun(run.RunId).Should().BeNull();
    }

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_WithSuccessfulRun_ReturnsTimestamp()
    {
        Directory.CreateDirectory(_runsDir);
        var completedAt = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        WriteRunFile("run-1", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Succeeded, completedAt);

        var sut = CreateSut();

        var result = await sut.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        result.Should().Be(completedAt);
    }

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_NoSuccessfulRun_ReturnsMinValue()
    {
        var sut = CreateSut();

        var result = await sut.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        result.Should().Be(DateTimeOffset.MinValue);
    }

    private static readonly JsonSerializerOptions RunFileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public async Task PrepareFeedbackDataAsync_WhenRunHistoryThrows_LogsAndDoesNotThrow()
    {
        // Force the catch path in PrepareFeedbackDataAsync by making the history service throw
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated history failure"));

        var sut = CreateSut();
        var run = CreateConsolidationRun();

        // Must not throw — the method swallows the exception and logs a warning
        var act = () => sut.PrepareFeedbackDataAsync(run, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    private void WriteRunFile(string runId, ConsolidationRunType type, ConsolidationRunStatus status, DateTimeOffset? completedAtUtc)
    {
        Directory.CreateDirectory(_runsDir);
        var json = JsonSerializer.Serialize(new
        {
            runId,
            type = type.ToString(),
            status = status.ToString(),
            startedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            completedAtUtc
        }, RunFileJsonOptions);
        File.WriteAllText(Path.Combine(_runsDir, $"{runId}.json"), json);
    }
}
