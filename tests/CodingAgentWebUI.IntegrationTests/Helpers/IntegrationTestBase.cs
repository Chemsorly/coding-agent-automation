using Moq;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.IntegrationTests.Helpers;

/// <summary>
/// Base class for integration tests that wire up real internal services
/// (JsonConfigurationStore, IssueDescriptionParser, CiLogWriter) with
/// mocked external boundaries (providers, quality gate validator).
/// Creates isolated temp directories per test and cleans up on dispose.
/// </summary>
public class IntegrationTestBase : IDisposable
{
    protected readonly string TempRoot;
    protected readonly string ConfigDir;
    protected readonly string RunsDir;
    protected readonly string WorkspaceBase;
    protected readonly JsonConfigurationStore ConfigStore;
    protected readonly Mock<IProviderFactory> MockFactory = new();
    protected readonly Mock<IIssueProvider> MockIssueProvider = new();
    protected readonly Mock<IRepositoryProvider> MockRepoProvider = new();
    protected readonly Mock<IAgentProvider> MockAgentProvider = new();
    protected readonly Mock<IQualityGateValidator> MockValidator = new();
    protected readonly Mock<Serilog.ILogger> MockLogger = new();

    protected IntegrationTestBase()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), $"integration-{Guid.NewGuid()}");
        ConfigDir = Path.Combine(TempRoot, "config");
        RunsDir = Path.Combine(TempRoot, "runs");
        WorkspaceBase = Path.Combine(TempRoot, "workspaces");
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(RunsDir);
        Directory.CreateDirectory(WorkspaceBase);

        ConfigStore = new JsonConfigurationStore(ConfigDir);
        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        MockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue",
                Description = "## Requirements\nImplement feature X\n\n## Acceptance Criteria\n- [ ] Feature X works",
                Labels = Array.Empty<string>()
            });
        MockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        MockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        MockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        MockRepoProvider.Setup(p => p.RepositoryFullName).Returns("test-org/test-repo");
        MockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        MockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        MockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        MockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        MockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        MockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        MockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    var dir = Path.Combine(req.WorkspacePath, ".agent");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "analysis.md"), new string('x', 200));
                    var assessment = new { recommendation = "ready", reason = "Test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() };
                    File.WriteAllText(Path.Combine(dir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(assessment, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
        MockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        MockAgentProvider.Setup(p => p.PipelineInjectedPaths)
            .Returns(new List<string> { ".kiro" });

        MockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            });

        MockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(MockIssueProvider.Object);
        MockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(MockRepoProvider.Object);
        MockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(MockAgentProvider.Object);
    }

    /// <summary>
    /// Saves default pipeline config and provider configs to the real JsonConfigurationStore,
    /// then creates a TestPipelineRunner wired to the real store.
    /// </summary>
    protected async Task<TestPipelineRunner> CreateServiceWithPersistedConfigAsync(
        PipelineConfiguration? config = null)
    {
        var pipelineConfig = config ?? new PipelineConfiguration { WorkspaceBaseDirectory = WorkspaceBase };
        await ConfigStore.SavePipelineConfigAsync(pipelineConfig, CancellationToken.None);

        await SaveProviderConfigsAsync();

        return new TestPipelineRunner(
            ConfigStore,
            MockFactory.Object,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(MockLogger.Object),
            new QualityGateExecutor(MockValidator.Object, new PullRequestOrchestrator(MockLogger.Object), new CiLogWriter(MockLogger.Object), new FeedbackService(MockLogger.Object), MockLogger.Object),
            MockLogger.Object,
            brainUpdateService: new BrainUpdateService(MockLogger.Object),
            historyService: new PipelineRunHistoryService(MockLogger.Object, RunsDir));
    }

    protected async Task SaveProviderConfigsAsync()
    {
        await ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test Issue" },
            CancellationToken.None);
        await ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test Repo" },
            CancellationToken.None);
        await ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test Agent",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "test-model" } },
            CancellationToken.None);
        await ConfigStore.SaveQualityGateConfigAsync(
            new QualityGateConfiguration { Id = "default", DisplayName = "Default", CompilationCommand = "dotnet", CompilationArguments = ["build"], TestCommand = "dotnet", TestArguments = ["test"], Enabled = true },
            CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
