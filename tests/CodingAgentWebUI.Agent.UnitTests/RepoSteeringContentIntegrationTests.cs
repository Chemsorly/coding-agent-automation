using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Integration test proving the end-to-end path for repo-level steering content:
/// Persisted ProviderConfig.SteeringContent → Config store round-trip → Dispatch lookup logic
/// → JobAssignmentMessage.RepoSteeringContent → WriteSteeringStep → File written to workspace.
///
/// This test integrates the Infrastructure persistence layer (JsonConfigurationStore),
/// the dispatch extraction logic (same LINQ as AgentJobDispatcher.Execution.cs:65),
/// and the Agent workspace write step (WriteSteeringStep) in a single flow.
///
/// Guards against regression of the dead code path documented in issue #1652, where
/// TokenVendingService.CloneWithSettings previously dropped SteeringContent.
///
/// TODO: This test does not exercise TokenVendingService.CloneWithSettings (the actual root cause).
/// A unit test on CloneWithSettings or an integration test including PrepareProviderConfigsAsync
/// would close the gap if TokenVendingService.cs:258 is accidentally modified.
/// </summary>
[Trait("Category", "Integration")]
public class RepoSteeringContentIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configDir;
    private readonly string _workspaceDir;
    private readonly JsonConfigurationStore _configStore;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    public RepoSteeringContentIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"steering-integration-{Guid.NewGuid()}");
        _configDir = Path.Combine(_tempRoot, "config");
        _workspaceDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_workspaceDir);

        _configStore = new JsonConfigurationStore(_configDir);
        _mockCallbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Proves the full integration path: persist a ProviderConfig with SteeringContent,
    /// load it from the real config store, simulate the dispatch lookup (same logic as
    /// AgentJobDispatcher.Execution.cs:65), build a JobAssignmentMessage, execute
    /// WriteSteeringStep, and verify the repo steering file is written to the workspace.
    /// </summary>
    [Fact]
    public async Task FullPath_PersistedSteeringContent_ReachesAgentWorkspaceViaWriteSteeringStep()
    {
        // Arrange: persist a repository ProviderConfig with SteeringContent
        const string repoProviderId = "repo-steering-test";
        const string expectedSteering = "# Repository Guidelines\n\nUse conventional commits.\nAlways run tests before pushing.";

        var repoConfig = new ProviderConfig
        {
            Id = repoProviderId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test-org/steering-repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Owner] = "test-org",
                [ProviderSettingKeys.Repo] = "steering-repo",
                [ProviderSettingKeys.BaseBranch] = "main"
            },
            SteeringContent = expectedSteering
        };

        await _configStore.SaveProviderConfigAsync(repoConfig, CancellationToken.None);

        // Act (Phase 1): Load from the config store — proves persistence round-trip
        var loadedConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        // Simulate the dispatch lookup logic from AgentJobDispatcher.Execution.cs:65
        // TODO: This LINQ is copy-pasted from the real dispatcher. If the dispatcher's lookup is modified
        // incorrectly, this test will still pass. Consider calling the actual dispatcher method to guard
        // against regressions in the dispatch extraction code path.
        // RepoSteeringContent = ctx.ProviderConfigs.FirstOrDefault(c => c.Id == ctx.RepoProviderId)?.SteeringContent
        var repoSteeringContent = loadedConfigs.FirstOrDefault(c => c.Id == repoProviderId)?.SteeringContent;

        // Assert (Phase 1): SteeringContent survived persistence
        repoSteeringContent.Should().NotBeNull("SteeringContent must survive config store round-trip");
        repoSteeringContent.Should().Be(expectedSteering);

        // Act (Phase 2): Build JobAssignmentMessage and execute WriteSteeringStep
        var job = new JobAssignmentMessage
        {
            JobId = "integration-test-run",
            IssueIdentifier = "1652",
            IssueDetail = new IssueDetail { Identifier = "1652", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = repoProviderId,
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = loadedConfigs,
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "integration-test",
            RepoSteeringContent = repoSteeringContent
        };

        var step = new WriteSteeringStep(job);
        var context = CreateStepContext(AgentProviderType.KiroCli);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert (Phase 2): File written to workspace with correct content
        result.Should().Be(StepResult.Continue);

        var repoFilePath = Path.Combine(_workspaceDir, AgentWorkspacePaths.KiroSteeringRepoFilePath);
        File.Exists(repoFilePath).Should().BeTrue("WriteSteeringStep must create the repo steering file");

        var fileContent = File.ReadAllText(repoFilePath);
        fileContent.Should().Contain("inclusion: always", "file should have Kiro frontmatter");
        fileContent.Should().Contain(expectedSteering, "file should contain the persisted steering content");
    }

    /// <summary>
    /// Proves that when no SteeringContent is configured on the ProviderConfig,
    /// the dispatch lookup produces null and WriteSteeringStep does not write a repo file.
    /// </summary>
    [Fact]
    public async Task FullPath_NullSteeringContent_NoRepoFileWritten()
    {
        // Arrange: persist a ProviderConfig without SteeringContent
        const string repoProviderId = "repo-no-steering";

        var repoConfig = new ProviderConfig
        {
            Id = repoProviderId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test-org/no-steering-repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Owner] = "test-org",
                [ProviderSettingKeys.Repo] = "no-steering-repo",
                [ProviderSettingKeys.BaseBranch] = "main"
            }
            // SteeringContent intentionally not set (null)
        };

        await _configStore.SaveProviderConfigAsync(repoConfig, CancellationToken.None);

        // Act: Load and simulate dispatch
        var loadedConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var repoSteeringContent = loadedConfigs.FirstOrDefault(c => c.Id == repoProviderId)?.SteeringContent;

        repoSteeringContent.Should().BeNull("null SteeringContent must survive round-trip as null");

        var job = new JobAssignmentMessage
        {
            JobId = "integration-test-null",
            IssueIdentifier = "1652",
            IssueDetail = new IssueDetail { Identifier = "1652", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = repoProviderId,
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = loadedConfigs,
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "integration-test",
            RepoSteeringContent = repoSteeringContent
        };

        var step = new WriteSteeringStep(job);
        var context = CreateStepContext(AgentProviderType.KiroCli);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert: no steering files created
        result.Should().Be(StepResult.Continue);
        var steeringDir = Path.Combine(_workspaceDir, ".kiro", "steering");
        Directory.Exists(steeringDir).Should().BeFalse("no steering directory should be created when content is null");
    }

    private PipelineStepContext CreateStepContext(AgentProviderType providerType)
    {
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.ProviderType).Returns(providerType);
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(
            providerType == AgentProviderType.KiroCli ? [".kiro"] : ["AGENTS.md"]);

        var run = new PipelineRun
        {
            RunId = "integration-test-run",
            IssueIdentifier = "1652",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            WorkspacePath = _workspaceDir,
            StartedAt = DateTime.UtcNow
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = new Mock<IRepositoryProvider>().Object,
            AgentProvider = mockAgent.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = new Mock<IConfigurationStore>().Object,
            Callbacks = _mockCallbacks.Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            AgentExecution = new Mock<IAgentPhaseExecutor>().Object,
            QualityGates = new Mock<IQualityGateExecutor>().Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
            Logger = _mockLogger.Object
        };
    }
}
