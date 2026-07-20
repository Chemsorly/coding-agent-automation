using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationService"/>.
/// Validates: Requirements 3.1, 3.3, 3.5, 3.7, 9.2, 9.4
/// </summary>
public sealed class ConsolidationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runsDir;
    private readonly string _suggestionsPath;
    private readonly ILogger _logger;
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly PipelineConfiguration _config;
    private readonly List<PipelineJobTemplate> _templates;

    public ConsolidationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"consolidation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _runsDir = Path.Combine(_tempDir, "runs");
        _suggestionsPath = Path.Combine(_tempDir, "harness-suggestions.json");

        _logger = new LoggerConfiguration().CreateLogger();
        _mockRunHistory = new Mock<IPipelineRunHistoryService>();
        // TODO: GetRunHistoryAsync always returns empty list — no test exercises PrepareFeedbackDataAsync with actual feedback entries. Add tests with non-empty run history to cover filtering logic after the async migration.
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>());

        _templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "DotNet Repo",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-1",
                BrainProviderId = "bp-1",
                Enabled = true
            },
            new()
            {
                Id = "tmpl-2",
                Name = "Python Repo",
                IssueProviderId = "ip-2",
                RepoProviderId = "rp-2",
                Enabled = true
            }
        };

        _config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempDir
        };

        // Mock IProjectStore to return a default project owning all templates
        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { "tmpl-1", "tmpl-2" }
                }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_templates);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private ConsolidationService CreateSut() => new(
        _logger,
        _config,
        _mockProjectStore.Object,
        _mockRunHistory.Object,
        new FileSystemConsolidationRunStore(_runsDir),
        new FileSystemHarnessSuggestionStore(_suggestionsPath));

    #region TriggerAsync — creates run and persists

    [Fact]
    public async Task TriggerAsync_ValidTemplate_CreatesRunWithRunningStatus()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ConsolidationRunStatus.Running);
        run.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        run.TemplateId.Should().Be("tmpl-1");
        run.TemplateName.Should().Be("DotNet Repo");
        run.RunId.Should().NotBeNullOrEmpty();
        run.StartedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TriggerAsync_ValidTemplate_PersistsRunToDisk()
    {
        // Validates: Requirement 3.1, 9.2
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        var filePath = Path.Combine(_runsDir, $"{run!.RunId}.json");
        File.Exists(filePath).Should().BeTrue();
    }

    #endregion

    #region TriggerAsync — duplicate running rejects

    [Fact]
    public async Task TriggerAsync_DuplicateRunning_ReturnsNull()
    {
        // Validates: Requirement 3.7
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        second.Should().BeNull();
    }

    [Fact]
    public async Task TriggerAsync_DifferentType_SameTemplate_Succeeds()
    {
        // Validates: Requirement 3.7 — different type is not rejected
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerAsync_SameType_DifferentTemplate_Succeeds()
    {
        // Validates: Requirement 3.7 — different templateId is not rejected
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-2", CancellationToken.None);
        second.Should().NotBeNull();
    }

    #endregion

    #region TriggerAsync — unknown template rejects

    [Fact]
    public async Task TriggerAsync_UnknownTemplateId_ReturnsNull()
    {
        // Validates: Requirement 3.5
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "nonexistent-template", CancellationToken.None);

        run.Should().BeNull();
    }

    #endregion

    #region UpdateRunAsync — persists status change

    [Fact]
    public async Task UpdateRunAsync_ChangesStatusAndSetsCompletedAt()
    {
        // Validates: Requirement 3.3
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        await sut.UpdateRunAsync(
            run!.RunId, ConsolidationRunStatus.Succeeded, "All done", CancellationToken.None);

        // Verify by reading back from history
        var history = await sut.GetRunHistoryAsync(CancellationToken.None);
        var updated = history.First(r => r.RunId == run.RunId);
        updated.Status.Should().Be(ConsolidationRunStatus.Succeeded);
        updated.Summary.Should().Be("All done");
        updated.CompletedAtUtc.Should().NotBeNull();
        updated.CompletedAtUtc!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdateRunAsync_RemovesFromRunningTracker_AllowsNewTrigger()
    {
        // Validates: Requirement 3.7 — after completion, same type+template can be triggered again
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        await sut.UpdateRunAsync(
            first!.RunId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        second.Should().NotBeNull();
    }

    #endregion

    #region GetRunHistoryAsync — returns ordered results

    [Fact]
    public async Task GetRunHistoryAsync_ReturnsRunsOrderedByStartedAtDescending()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var run1 = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        await Task.Delay(50); // Ensure different timestamps
        var run2 = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);

        var history = await sut.GetRunHistoryAsync(CancellationToken.None);

        history.Should().HaveCount(2);
        history[0].RunId.Should().Be(run2!.RunId); // Most recent first
        history[1].RunId.Should().Be(run1!.RunId);
    }

    [Fact]
    public async Task GetRunHistoryAsync_EmptyDirectory_ReturnsEmptyList()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var history = await sut.GetRunHistoryAsync(CancellationToken.None);

        history.Should().BeEmpty();
    }

    #endregion

    #region GetLastRunAsync — filters correctly

    [Fact]
    public async Task GetLastRunAsync_ReturnsOnlyMatchingTypeAndTemplate()
    {
        // Validates: Requirement 9.4
        var sut = CreateSut();

        // Create runs of different types and templates
        var brain1 = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        var refactor1 = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);
        var brain2 = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-2", CancellationToken.None);

        var result = await sut.GetLastRunAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.RunId.Should().Be(brain1!.RunId);
        result.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        result.TemplateId.Should().Be("tmpl-1");
    }

    [Fact]
    public async Task GetLastRunAsync_NoMatch_ReturnsNull()
    {
        // Validates: Requirement 9.4
        var sut = CreateSut();

        await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        var result = await sut.GetLastRunAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-2", CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region Harness suggestions — read/write round-trip

    [Fact]
    public async Task SaveAndGetHarnessSuggestions_RoundTripsCorrectly()
    {
        // Validates: Requirement 9.2
        var sut = CreateSut();

        var suggestions = new HarnessSuggestions
        {
            GeneratedAtUtc = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc),
            BasedOnRunCount = 42,
            SuccessRate = 0.85m,
            Suggestions = new List<HarnessSuggestion>
            {
                new()
                {
                    Text = "Increase agent timeout for complex repos",
                    Rationale = "3 out of 5 failures were timeout-related",
                    Frequency = 3
                },
                new()
                {
                    Text = "Add file context for config files",
                    Rationale = "Agents frequently miss appsettings.json",
                    Frequency = 7
                }
            }
        };

        await sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);
        var loaded = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.GeneratedAtUtc.Should().Be(suggestions.GeneratedAtUtc);
        loaded.BasedOnRunCount.Should().Be(42);
        loaded.SuccessRate.Should().Be(0.85m);
        loaded.Suggestions.Should().HaveCount(2);
        loaded.Suggestions[0].Text.Should().Be("Increase agent timeout for complex repos");
        loaded.Suggestions[0].Frequency.Should().Be(3);
        loaded.Suggestions[1].Text.Should().Be("Add file context for config files");
        loaded.Suggestions[1].Frequency.Should().Be(7);
    }

    [Fact]
    public async Task GetHarnessSuggestionsAsync_FileDoesNotExist_ReturnsNull()
    {
        // Validates: Requirement 9.2
        var sut = CreateSut();

        var result = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region TriggerAsync — global (null templateId) for harness suggestions

    [Fact]
    public async Task TriggerAsync_NullTemplateId_CreatesGlobalRun()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        run.Should().NotBeNull();
        run!.TemplateId.Should().BeNull();
        run.TemplateName.Should().Be("Global");
        run.Type.Should().Be(ConsolidationRunType.HarnessSuggestions);
    }

    #endregion

    // --- DeletePersistedRunAsync tests ---

    [Fact]
    public async Task DeletePersistedRunAsync_FileExists_DeletesFile()
    {
        // Arrange
        var sut = CreateSut();
        var run = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();
        var filePath = Path.Combine(_runsDir, $"{run!.RunId}.json");
        File.Exists(filePath).Should().BeTrue();

        // Act
        await sut.DeletePersistedRunAsync(run.RunId);

        // Assert
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeletePersistedRunAsync_FileDoesNotExist_DoesNotThrow()
    {
        // Arrange
        var sut = CreateSut();
        Directory.CreateDirectory(_runsDir);

        // Act & Assert — no exception
        await sut.DeletePersistedRunAsync(Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task DeletePersistedRunAsync_NullRunId_ThrowsArgumentNullException()
    {
        var sut = CreateSut();
        await sut.Invoking(s => s.DeletePersistedRunAsync(null!))
            .Should().ThrowExactlyAsync<ArgumentNullException>();
    }

    // --- GetLastSuccessfulHarnessRunTimestampAsync tests ---

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_WithSuccessfulRuns_ReturnsLatestTimestamp()
    {
        // Arrange
        var sut = CreateSut();
        Directory.CreateDirectory(_runsDir);

        var olderTimestamp = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var newerTimestamp = new DateTime(2026, 3, 15, 8, 30, 0, DateTimeKind.Utc);

        WriteConsolidationRunFile("run-1", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Succeeded, olderTimestamp);
        WriteConsolidationRunFile("run-2", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Succeeded, newerTimestamp);
        WriteConsolidationRunFile("run-3", ConsolidationRunType.BrainConsolidation, ConsolidationRunStatus.Succeeded, newerTimestamp.AddDays(1));

        // Act
        var result = await sut.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        // Assert — returns the latest HarnessSuggestions succeeded timestamp, not the brain one
        result.Should().Be(newerTimestamp);
    }

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_NoRuns_ReturnsMinValue()
    {
        // Arrange
        var sut = CreateSut();
        // Don't create the directory — simulates first run

        // Act
        var result = await sut.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        // Assert
        result.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_OnlyFailedRuns_ReturnsMinValue()
    {
        // Arrange
        var sut = CreateSut();
        Directory.CreateDirectory(_runsDir);
        WriteConsolidationRunFile("run-1", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Failed, DateTimeOffset.UtcNow);

        // Act
        var result = await sut.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        // Assert
        result.Should().Be(DateTimeOffset.MinValue);
    }

    private void WriteConsolidationRunFile(string runId, ConsolidationRunType type, ConsolidationRunStatus status, DateTimeOffset? completedAtUtc)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            runId,
            type = type.ToString(),
            status = status.ToString(),
            startedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            completedAtUtc
        }, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(Path.Combine(_runsDir, $"{runId}.json"), json);
    }

    #region TriggerAsync — persist failure rollback (Req 8.1)

    [Fact]
    public async Task TriggerAsync_WhenPersistFails_ReturnsNull()
    {
        // Validates: Requirement 8.1 — TriggerAsync returns null when persist fails.
        // PersistRunAsync internally swallows exceptions. When persist fails silently,
        // TriggerAsync still returns a run (no exception escapes). However, if we force
        // PersistRunAsync to throw (by making the consolidation runs directory path point
        // to a location that AtomicFileWriter.WriteAsync cannot write to), the try/catch
        // in TriggerAsync should catch, roll back, and return null.
        //
        // Strategy: Create a FILE at the path where the runs directory should be.
        // This causes Directory.CreateDirectory inside AtomicFileWriter to throw IOException.
        // Since PersistRunAsync catches this, the exception won't propagate to TriggerAsync.
        // So this test verifies the observable behavior: run IS still returned (persist silently failed).
        // The actual rollback is tested by verifying the _runningRuns concurrency guard behavior.

        // Use a path that's a file (not directory) to block AtomicFileWriter's Directory.CreateDirectory
        var blockerDir = Path.Combine(_tempDir, "blocked-runs");
        File.WriteAllText(blockerDir, "I am a file, not a directory");

        var sut = new ConsolidationService(
            _logger,
            _config,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            new FileSystemConsolidationRunStore(blockerDir),
            new FileSystemHarnessSuggestionStore(_suggestionsPath));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        // PersistRunAsync now throws on failure → TriggerAsync catches, rolls back, returns null
        run.Should().BeNull();
    }

    [Fact]
    public async Task TriggerAsync_WhenPersistFailsAndDispatcherFails_RollsBackRunningRuns()
    {
        // Validates: Requirement 8.1 — When dispatch fails after persist failure,
        // the concurrency guard is released so the same type+template can be re-triggered.

        var mockDispatcher = new Mock<IConsolidationDispatcher>();
        mockDispatcher
            .Setup(d => d.TryDispatchAsync(
                It.IsAny<ConsolidationRun>(),
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsolidationDispatchResult.Failed);

        var sut = new ConsolidationService(
            _logger,
            _config,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            new FileSystemConsolidationRunStore(_runsDir),
            new FileSystemHarnessSuggestionStore(_suggestionsPath),
            mockDispatcher.Object);

        // First trigger — dispatch fails → rollback removes from _runningRuns
        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().BeNull();

        // Second trigger — should NOT be rejected as duplicate (concurrency guard was released)
        mockDispatcher
            .Setup(d => d.TryDispatchAsync(
                It.IsAny<ConsolidationRun>(),
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsolidationDispatchResult.Dispatched);

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerAsync_WhenDispatcherThrows_RollsBackRunningRunsAndRethrows()
    {
        // Validates: Requirement 8.1 — When dispatcher throws, TriggerAsync removes
        // the entry from _runningRuns and re-throws so the caller can display the real error.

        var mockDispatcher = new Mock<IConsolidationDispatcher>();
        mockDispatcher
            .Setup(d => d.TryDispatchAsync(
                It.IsAny<ConsolidationRun>(),
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated dispatch failure"));

        var sut = new ConsolidationService(
            _logger,
            _config,
            _mockProjectStore.Object,
            _mockRunHistory.Object,
            new FileSystemConsolidationRunStore(_runsDir),
            new FileSystemHarnessSuggestionStore(_suggestionsPath),
            mockDispatcher.Object);

        // TriggerAsync cleans up state and re-throws the dispatch exception
        var act = () => sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated dispatch failure");

        // Verify rollback: a subsequent trigger for the same type+template succeeds
        mockDispatcher
            .Setup(d => d.TryDispatchAsync(
                It.IsAny<ConsolidationRun>(),
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsolidationDispatchResult.Dispatched);

        var retryResult = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        retryResult.Should().NotBeNull();
    }

    #endregion
}
