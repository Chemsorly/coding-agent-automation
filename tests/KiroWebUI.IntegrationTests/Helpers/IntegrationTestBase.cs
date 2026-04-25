using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Infrastructure;
using KiroWebUI.Infrastructure.GitHub;
using KiroWebUI.Infrastructure.Git;
using KiroWebUI.Infrastructure.Persistence;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.IntegrationTests.Helpers;

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
        MockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue",
                Description = "## Requirements\nImplement feature X\n\n## Acceptance Criteria\n- [ ] Feature X works",
                Labels = Array.Empty<string>()
            });
        MockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        MockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        MockRepoProvider.Setup(p => p.RepositoryFullName).Returns("test-org/test-repo");
        MockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        MockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        MockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        MockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        MockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        MockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        MockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        MockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        MockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
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
    /// then creates a PipelineOrchestrationService wired to the real store.
    /// </summary>
    protected async Task<PipelineOrchestrationService> CreateServiceWithPersistedConfigAsync(
        PipelineConfiguration? config = null)
    {
        var pipelineConfig = config ?? new PipelineConfiguration { WorkspaceBaseDirectory = WorkspaceBase };
        await ConfigStore.SavePipelineConfigAsync(pipelineConfig, CancellationToken.None);

        await SaveProviderConfigsAsync();

        return new PipelineOrchestrationService(
            ConfigStore,
            MockFactory.Object,
            new IssueDescriptionParser(),
            MockValidator.Object,
            new CiLogWriter(MockLogger.Object),
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
                Settings = new Dictionary<string, string> { ["model"] = "test-model" } },
            CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(TempRoot))
            Directory.Delete(TempRoot, recursive: true);
    }
}
