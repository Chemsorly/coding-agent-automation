using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.UnitTests.Helpers;
using CodingAgentWebUI.Pipeline; // TODO: Redundant — namespace is implicitly accessible from child namespace CodingAgentWebUI.Pipeline.UnitTests

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for PipelineOrchestrationService.CreateDispatchedRunAsync and RemoveAllAgentLabelsAsync.
/// </summary>
// TODO: Implement IAsyncLifetime (or IDisposable) to dispose PipelineOrchestrationService instances — they own a SemaphoreSlim via IDisposable/IAsyncDisposable.
public class PipelineOrchestrationServiceDispatchTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IOrchestratorRunService> _mockRunService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineOrchestrationService _service;

    public PipelineOrchestrationServiceDispatchTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockRunService = new Mock<IOrchestratorRunService>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
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

        _mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("owner/repo");
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);
        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_mockIssueProvider.Object);

        _mockRunService.Setup(r => r.IsIssueBeingProcessed(It.IsAny<string>())).Returns(false);

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(new List<PipelineRunSummary>().AsReadOnly());

        var mockValidator = new Mock<IQualityGateValidator>();
        var mockAgentProvider = new Mock<IAgentProvider>();
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgentProvider.Object);

        _service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(_mockLogger.Object),
            new QualityGateOrchestrator(mockValidator.Object, new PullRequestOrchestrator(_mockLogger.Object), _mockLogger.Object),
            _mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object,
            runService: _mockRunService.Object);
    }

    // --- CreateDispatchedRunAsync tests ---

    [Fact]
    public async Task CreateDispatchedRunAsync_Success_ReturnsRunWithCorrectProperties()
    {
        // Act
        var run = await _service.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "42", "agent-1", "agent-container-1", CancellationToken.None,
            initiatedBy: "closed-loop");

        // Assert
        run.Should().NotBeNull();
        run!.IssueIdentifier.Should().Be("42");
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
        _mockRunService.Setup(r => r.IsIssueBeingProcessed("42")).Returns(true);

        // Act
        var run = await _service.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "42", "agent-1", "agent-container-1", CancellationToken.None);

        // Assert
        run.Should().BeNull();
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_NullIssueIdentifier_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.CreateDispatchedRunAsync("issue-1", "repo-1", null!, "agent-1", "agent-container-1", CancellationToken.None));
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_NullAgentId_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.CreateDispatchedRunAsync("issue-1", "repo-1", "42", "agent-1", null!, CancellationToken.None));
    }

    // --- RemoveAllAgentLabelsAsync tests ---

    [Fact]
    public async Task RemoveAllAgentLabelsAsync_RemovesAllLabels()
    {
        // Arrange: run a pipeline to initialize providers (ActiveIssueProvider gets set)
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = Array.Empty<string>() });
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());
        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42");
        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>() as IReadOnlyList<LinkedPullRequest>);
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        _mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        var mockAgentProvider = new Mock<IAgentProvider>();
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgentProvider.Object);
        mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockAgentProvider.Setup(p => p.GetHealthStatus()).Returns(new AgentHealthStatus { IsExecuting = false });
        mockAgentProvider.Setup(p => p.GetLatestSessionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    var analysisDir = Path.Combine(req.WorkspacePath, ".agent");
                    Directory.CreateDirectory(analysisDir);
                    File.WriteAllText(Path.Combine(analysisDir, "analysis.md"), new string('x', 200));
                    File.WriteAllText(Path.Combine(analysisDir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { recommendation = "ready", reason = "ok", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>(), plannedApproach = "test", estimatedComplexity = "low" }));
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var mockQgValidator = new Mock<IQualityGateValidator>();
        mockQgValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(new List<PipelineRunSummary>().AsReadOnly());
        mockHistoryService.Setup(h => h.AddRunToHistory(It.IsAny<PipelineRun>()));
        mockHistoryService.Setup(h => h.TryDeleteWorkspace(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()));

        var service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(_mockLogger.Object),
            new QualityGateOrchestrator(mockQgValidator.Object, new PullRequestOrchestrator(_mockLogger.Object), _mockLogger.Object),
            _mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object,
            runService: _mockRunService.Object);

        // Run pipeline to completion — this sets up ActiveIssueProvider
        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);
        run.CurrentStep.Should().Be(PipelineStep.Completed);

        // Reset mock to track only the direct call
        _mockIssueProvider.Invocations.Clear();

        // Act: call RemoveAllAgentLabelsAsync directly
        await service.RemoveAllAgentLabelsAsync("42", CancellationToken.None);

        // Assert: RemoveLabelAsync was called for each label in AgentLabels.All
        foreach (var label in AgentLabels.All)
        {
            _mockIssueProvider.Verify(
                p => p.RemoveLabelAsync("42", label, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task RemoveAllAgentLabelsAsync_WhenProviderThrows_ExceptionSwallowed()
    {
        // Arrange: same setup but RemoveLabelAsync throws
        _mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API error"));
        _mockIssueProvider.Setup(p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = Array.Empty<string>() });
        _mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());
        _mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42");
        _mockRepoProvider.Setup(p => p.GetAgentPullRequestsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LinkedPullRequest>() as IReadOnlyList<LinkedPullRequest>);
        _mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        _mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        var mockAgentProvider = new Mock<IAgentProvider>();
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgentProvider.Object);
        mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockAgentProvider.Setup(p => p.GetHealthStatus()).Returns(new AgentHealthStatus { IsExecuting = false });
        mockAgentProvider.Setup(p => p.GetLatestSessionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    var analysisDir = Path.Combine(req.WorkspacePath, ".agent");
                    Directory.CreateDirectory(analysisDir);
                    File.WriteAllText(Path.Combine(analysisDir, "analysis.md"), new string('x', 200));
                    File.WriteAllText(Path.Combine(analysisDir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { recommendation = "ready", reason = "ok", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>(), plannedApproach = "test", estimatedComplexity = "low" }));
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var mockQgValidator = new Mock<IQualityGateValidator>();
        mockQgValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(new List<PipelineRunSummary>().AsReadOnly());
        mockHistoryService.Setup(h => h.AddRunToHistory(It.IsAny<PipelineRun>()));
        mockHistoryService.Setup(h => h.TryDeleteWorkspace(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()));

        var service = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(_mockLogger.Object),
            new QualityGateOrchestrator(mockQgValidator.Object, new PullRequestOrchestrator(_mockLogger.Object), _mockLogger.Object),
            _mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object,
            runService: _mockRunService.Object);

        // Run pipeline — it will fail during label swap but that's handled gracefully
        await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Act: call RemoveAllAgentLabelsAsync directly — should not throw
        var act = () => service.RemoveAllAgentLabelsAsync("99", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
