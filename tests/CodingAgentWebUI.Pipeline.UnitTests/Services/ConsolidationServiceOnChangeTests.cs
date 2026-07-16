using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies that ConsolidationService fires OnChange at the correct times.
/// The Blazor UI (AgentMonitoring, Consolidation pages) depends on OnChange to refresh.
/// If OnChange doesn't fire, the frontend shows stale data.
/// </summary>
public sealed class ConsolidationServiceOnChangeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConsolidationService _sut;
    private readonly List<string> _onChangeLog = new();

    public ConsolidationServiceOnChangeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onchange-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

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

        _sut = new ConsolidationService(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            mockProjectStore.Object,
            mockHistory.Object,
            new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs")),
            new FileSystemHarnessSuggestionStore(Path.Combine(_tempDir, "h.json")));

        _sut.OnChange += () => _onChangeLog.Add(DateTime.UtcNow.ToString("O"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task TriggerAsync_FiresOnChange()
    {
        await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);

        _onChangeLog.Should().NotBeEmpty("TriggerAsync must fire OnChange so UI shows the new run");
    }

    [Fact]
    public async Task UpdateRunAsync_FiresOnChange()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);
        _onChangeLog.Clear();

        await _sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

        _onChangeLog.Should().NotBeEmpty("UpdateRunAsync must fire OnChange so UI removes the run from active list");
    }

    [Fact]
    public async Task CancelQueuedRunAsync_FiresOnChange()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, "t1", CancellationToken.None);
        run!.Status = ConsolidationRunStatus.Queued;
        var store = new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs"));
        await store.SaveRunAsync(run, CancellationToken.None);
        _onChangeLog.Clear();

        await _sut.CancelQueuedRunAsync(run.RunId, CancellationToken.None);

        _onChangeLog.Should().NotBeEmpty("CancelQueuedRunAsync must fire OnChange so UI reflects cancellation");
    }

    [Fact]
    public async Task TransitionToRunningAsync_FiresOnChange()
    {
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);
        run!.Status = ConsolidationRunStatus.Queued;
        var store = new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs"));
        await store.SaveRunAsync(run, CancellationToken.None);
        _onChangeLog.Clear();

        await _sut.TransitionToRunningAsync(run.RunId, CancellationToken.None);

        _onChangeLog.Should().NotBeEmpty("TransitionToRunningAsync must fire OnChange so UI shows status change");
    }

    [Fact]
    public async Task SaveHarnessSuggestionsAsync_FiresOnChange()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 1, GeneratedAtUtc = DateTime.UtcNow, SuccessRate = 1.0m,
            Suggestions = new List<HarnessSuggestion>()
        };
        _onChangeLog.Clear();

        await _sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);

        _onChangeLog.Should().NotBeEmpty("SaveHarnessSuggestionsAsync must fire OnChange so Consolidation page refreshes");
    }

    [Fact]
    public async Task UpdateRunAsync_NonExistentRun_DoesNotFireOnChange()
    {
        _onChangeLog.Clear();

        await _sut.UpdateRunAsync(Guid.NewGuid().ToString(), ConsolidationRunStatus.Failed, "x", CancellationToken.None);

        _onChangeLog.Should().BeEmpty("OnChange must NOT fire when update has no effect (prevents unnecessary UI re-renders)");
    }
}
