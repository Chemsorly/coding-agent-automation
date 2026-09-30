using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
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

        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-onchange-test", ErrorMessage: null));

        _sut = new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir, DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" },
            mockProjectStore.Object,
            new Mock<IConsolidationRunStore>().Object,
            new InMemoryHarnessSuggestionStore(),
            new Mock<IProviderConfigStore>().Object,
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
}
