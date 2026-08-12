using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Orchestration;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for DispatchRunCreationService.CreateDispatchedRunAsync.
/// </summary>
public class PipelineOrchestrationServiceDispatchTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IOrchestratorRunService> _mockRunService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _service;

    public PipelineOrchestrationServiceDispatchTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockRunService = new Mock<IOrchestratorRunService>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test Repo" }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test Agent",
                    Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-sonnet" } }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test Issue" }
            });
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        _mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("owner/repo");
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_mockIssueProvider.Object);

        // TODO: This mocks IsIssueBeingProcessed to always return false, so CreateDispatchedRunAsync_Success
        // would pass even if RegisterDispatchedRun was broken. Add a Verify call to confirm AddRun is invoked.
        _mockRunService.Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>())).Returns(false);

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>().AsReadOnly());

        var mockAgentProvider = new Mock<IAgentProvider>();
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgentProvider.Object);

        var lifecycle = new PipelineRunLifecycleService(mockHistoryService.Object, _mockRunService.Object, _mockLogger.Object);

        _service = new DispatchRunCreationService(
            lifecycle,
            _mockConfigStore.Object,
            _mockFactory.Object,
            _mockLogger.Object);
    }

    // --- CreateDispatchedRunAsync tests ---

    [Fact]
    public async Task CreateDispatchedRunAsync_Success_ReturnsRunWithCorrectProperties()
    {
        // Act
        var run = await _service.CreateDispatchedRunAsync(
            new DispatchRunRequest
            {
                IssueProviderId = "issue-1",
                RepoProviderId = "repo-1",
                IssueIdentifier = "42",
                AgentProviderId = "agent-1",
                AgentId = "agent-container-1",
                InitiatedBy = "closed-loop"
            },
            CancellationToken.None);

        // Assert
        run.Should().NotBeNull();
        run!.IssueIdentifier.Value.Should().Be("42");
        run.CurrentStep.Should().Be(PipelineStep.Created);
        run.AgentId.Should().Be("agent-container-1");
        run.RepositoryName.Should().Be("owner/repo");
        run.ModelName.Should().Be("claude-sonnet");
        run.InitiatedBy.Should().Be("closed-loop");
        run.IssueProviderConfigId.Should().Be("issue-1");
        run.RepoProviderConfigId.Should().Be("repo-1");
        run.AgentProviderConfigId.Should().Be("agent-1");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_IssueAlreadyProcessed_ReturnsNull()
    {
        // Arrange
        _mockRunService.Setup(r => r.IsIssueBeingProcessed("42", It.IsAny<ProviderConfigId>())).Returns(true);

        // Act
        var run = await _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-container-1" },
            CancellationToken.None);

        // Assert
        run.Should().BeNull();
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_NullIssueIdentifier_ThrowsArgumentException()
    {
        // Act & Assert — IssueIdentifier is now a struct; default(IssueIdentifier) with null Value
        // throws ArgumentNullException (from ThrowIfNullOrEmpty with null argument), which derives from ArgumentException
        // TODO: [WARNING] ThrowsAnyAsync<ArgumentException> is weaker than necessary — use
        // Assert.ThrowsAsync<ArgumentNullException> since default(IssueIdentifier).Value is null,
        // which specifically triggers ArgumentNullException. ThrowsAnyAsync would also pass if an
        // unrelated ArgumentException fired first (e.g., a provider ID validation), masking guard order regressions.
        // TODO: [WARNING] No test covers the new empty-string rejection path. The guard changed from
        // ArgumentNullException.ThrowIfNull(issueIdentifier) (catches null only) to
        // ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value) (catches null AND empty string).
        // Add a test case using new IssueIdentifier("") (empty Value) to cover the empty-string path
        // across all migrated entry points (TryDispatchAsync, IsIssueQueued, MarkIssueComplete, etc.).
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _service.CreateDispatchedRunAsync(
                new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = default, AgentProviderId = "agent-1", AgentId = "agent-container-1" },
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_NullAgentId_CreatesRunWithNullAgentId()
    {
        // Act — null agentId is valid (used during dispatch window before agent resolution)
        var run = await _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = null },
            CancellationToken.None);

        // Assert
        run.Should().NotBeNull();
        run!.AgentId.Should().BeNull();
        run.IssueIdentifier.Value.Should().Be("42");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _service.DisposeAsync();
    }
}
