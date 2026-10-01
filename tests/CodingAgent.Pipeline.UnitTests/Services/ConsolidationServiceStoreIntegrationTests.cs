using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Integration tests verifying ConsolidationService correctly delegates harness suggestion
/// persistence to IHarnessSuggestionStore.
/// </summary>
public sealed class ConsolidationServiceStoreIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly PipelineConfiguration _config;
    private readonly IHarnessSuggestionStore _harnessStore;
    private readonly ConsolidationService _sut;

    public ConsolidationServiceStoreIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"store-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir, DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" };

        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { "tmpl-1" }
                }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "tmpl-1", Name = "Test Template", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true }
            });

        _harnessStore = new InMemoryHarnessSuggestionStore();

        _sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                new LoggerConfiguration().CreateLogger(),
                _config,
                _mockProjectStore.Object,
                _harnessStore,
                new Mock<IProviderConfigStore>().Object));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Verify harness suggestions round-trip through IHarnessSuggestionStore.
    /// </summary>
    [Fact]
    public async Task HarnessSuggestions_SaveAndGet_RoundTripsViaStore()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 10,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.85m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Frequency = 5, Rationale = "Test", Text = "Improve X" }
            }
        };

        await _sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);
        var loaded = await _sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.BasedOnRunCount.Should().Be(10);
        loaded.Suggestions.Should().HaveCount(1);
    }
}
