using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies ConsolidationService delegates to IHarnessSuggestionStore
/// by using mocks. Ensures no filesystem I/O happens inside the service itself.
///
/// IConsolidationRunStore was removed in issue #3031 — ConsolidationService no longer
/// holds a reference to it. Tests that previously verified DeleteRunAsync / SaveRunAsync
/// delegation have been removed. DeleteRunAsync itself was removed from ConsolidationService
/// along with IConsolidationRunStore.
/// </summary>
public sealed class ConsolidationServiceStoreDelegationTests
{
    private readonly Mock<IHarnessSuggestionStore> _mockHarnessStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();

    public ConsolidationServiceStoreDelegationTests()
    {
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new List<string> { "t1", "t2" } }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "Template1", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true },
                new() { Id = "t2", Name = "Template2", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true }
            });
    }

    private ConsolidationService CreateSut()
    {
        // WorkDistributor returns success so TriggerAsync can proceed.
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-delegation-test", ErrorMessage: null));

        return new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            _mockProjectStore.Object,
            _mockHarnessStore.Object,
            new Mock<IProviderConfigStore>().Object,
            WorkDistributor: mockWorkDistributor.Object));
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
        _mockHarnessStore.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var result = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        result.Should().BeSameAs(expected);
        _mockHarnessStore.Verify(s => s.LoadAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    /// <summary>
    /// TriggerAsync must not call SaveAsync on the IHarnessSuggestionStore as a side-effect of triggering.
    /// Issue #3028: TriggerAsync no longer persists to any store; the PipelineRun is the authoritative record.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DoesNotWriteToHarnessSuggestionStore()
    {
        var sut = CreateSut();

        await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);

        _mockHarnessStore.Verify(
            s => s.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "TriggerAsync must not write to the HarnessSuggestion store as a side-effect of triggering");
    }

    /// <summary>
    /// TriggerAsync duplicate (AlreadyExists=true from API 409) must return null
    /// and must not write to the store.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DuplicateRejected_DoesNotWriteToStore()
    {
        // Simulate the second trigger receiving a 409 (AlreadyExists) response.
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: null, ErrorMessage: null, AlreadyExists: true));

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), DefaultRequiredAgentLabels = "kiro" },
            _mockProjectStore.Object,
            _mockHarnessStore.Object,
            new Mock<IProviderConfigStore>().Object,
            WorkDistributor: mockWorkDistributor.Object));

        var result = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation,
            new TemplateId("t1"),
            CancellationToken.None);

        result.Should().BeNull("AlreadyExists=true must result in a null return (409 duplicate path)");
        _mockHarnessStore.Verify(
            s => s.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
