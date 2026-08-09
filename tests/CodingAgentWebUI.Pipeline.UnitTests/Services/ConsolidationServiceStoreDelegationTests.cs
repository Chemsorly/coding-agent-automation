using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
        await sut.CleanupOrphanedRunsAsync(CancellationToken.None);

        _mockRunStore.Verify(s => s.SaveRunAsync(It.Is<ConsolidationRun>(r =>
            r.RunId == orphan.RunId && r.Status == ConsolidationRunStatus.Failed), It.IsAny<CancellationToken>()), Times.Once);
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
}
