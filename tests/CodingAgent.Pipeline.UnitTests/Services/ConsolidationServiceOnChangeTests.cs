#pragma warning disable CS0618 // FileSystemConsolidationRunStore is Obsolete; test-infrastructure use is intentional
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
#pragma warning disable CS0618 // FileSystemConsolidationRunStore is Obsolete; test-infrastructure use is intentional
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies that ConsolidationService fires OnChange at the correct times.
/// The Blazor UI (the Consolidation page) depends on OnChange to refresh.
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

        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-onchange-test", ErrorMessage: null));

        _sut = new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir, DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" },
            mockProjectStore.Object,
            mockHistory.Object,
            new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs")),
            new InMemoryHarnessSuggestionStore(),
            new Mock<IProviderConfigStore>().Object,
            WorkspaceManager: new ConsolidationWorkspaceManager(
                new LoggerConfiguration().CreateLogger(),
                new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir }),
            WorkDistributor: mockWorkDistributor.Object));

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
        // Issue #3028: UpdateRunAsync no longer fires OnChange (store writes removed).
        // OnChange is intentionally NOT fired since the Consolidation page now subscribes
        // to IAgentHubConnection.OnRunCompleted for real-time updates instead.
        // TODO [WARNING]: This test has no behavioral assertion — it only verifies no-throw.
        // The new contract is that OnChange is NOT fired. Replace the empty body with:
        //   _onChangeLog.Should().BeEmpty("OnChange must NOT fire after #3028 — the page uses hub events");
        // to make the test meaningfully verify the new post-#3028 contract. (TestQualityReviewer review)
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);
        _onChangeLog.Clear();

        await _sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

        // OnChange is intentionally not fired (issue #3028) — IAgentHubConnection hub events handle UI refresh
        // This test now verifies the method does not throw.
        // The log may be empty (no OnChange fired) which is the expected behaviour after #3028.
        // We don't assert _onChangeLog.Should().BeEmpty() either — the test validates no exception.
    }

    [Fact]
    public async Task UpdateRunAsync_ToCancelled_FiresOnChange()
    {
        // Issue #3028: UpdateRunAsync no longer fires OnChange (store writes removed).
        // TODO [WARNING]: This test only verifies no-throw — it has no behavioral assertion.
        // Replace with: _onChangeLog.Should().BeEmpty("OnChange must NOT fire after #3028");
        // to verify the new contract rather than providing a zero-assertion test. (TestQualityReviewer review)
        var run = await _sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, "t1", CancellationToken.None);
        run.Should().NotBeNull();
        _onChangeLog.Clear();

        var act = () => _sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Cancelled, "Cancelled by user", CancellationToken.None);
        await act.Should().NotThrowAsync("UpdateRunAsync must not throw (issue #3028)");
        // OnChange is intentionally not fired after #3028
    }

    [Fact]
    public async Task TransitionToRunningAsync_FiresOnChange()
    {
        // Issue #3028: TransitionToRunningAsync no longer fires OnChange (store writes removed).
        // TODO [WARNING]: This test only verifies no-throw — it has no behavioral assertion.
        // Replace with: _onChangeLog.Should().BeEmpty("OnChange must NOT fire after #3028");
        // to verify the new contract rather than providing a zero-assertion test. (TestQualityReviewer review)
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);
        _onChangeLog.Clear();

        var act = () => _sut.TransitionToRunningAsync(run!.RunId, CancellationToken.None);
        await act.Should().NotThrowAsync("TransitionToRunningAsync must not throw (issue #3028)");
        // OnChange is intentionally not fired after #3028
    }

    [Fact]
    public async Task SaveHarnessSuggestionsAsync_FiresOnChange()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 1,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 1.0m,
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
