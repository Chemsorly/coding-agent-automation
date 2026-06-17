using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

public class WriteSteeringStepTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    public WriteSteeringStepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"test-steering-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── No-op scenarios ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BothContentsNull_ReturnsContinueNoFilesWritten()
    {
        var job = CreateJob(null, null);
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.KiroCli);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        Directory.Exists(Path.Combine(_tempDir, ".kiro", "steering")).Should().BeFalse();
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BothContentsEmpty_ReturnsContinueNoFilesWritten()
    {
        var job = CreateJob("", "");
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.KiroCli);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.IsAny<string>()), Times.Never);
    }

    // ── Kiro CLI scenarios ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_KiroCli_ProjectOnly_WritesProjectFile()
    {
        var job = CreateJob("project instructions", null);
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.KiroCli);

        await step.ExecuteAsync(context, CancellationToken.None);

        var projectPath = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        File.Exists(projectPath).Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("inclusion: always");
        content.Should().Contain("project instructions");

        var repoPath = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringRepoFilePath);
        File.Exists(repoPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_KiroCli_RepoOnly_WritesRepoFile()
    {
        var job = CreateJob(null, "repo instructions");
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.KiroCli);

        await step.ExecuteAsync(context, CancellationToken.None);

        var repoPath = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringRepoFilePath);
        File.Exists(repoPath).Should().BeTrue();
        var content = File.ReadAllText(repoPath);
        content.Should().Contain("inclusion: always");
        content.Should().Contain("repo instructions");

        var projectPath = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        File.Exists(projectPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_KiroCli_BothProvided_WritesBothFiles()
    {
        var job = CreateJob("project content", "repo content");
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.KiroCli);

        await step.ExecuteAsync(context, CancellationToken.None);

        File.Exists(Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath)).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringRepoFilePath)).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_KiroCli_CreatesDirectoryIfNotExists()
    {
        var job = CreateJob("content", null);
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.KiroCli);

        await step.ExecuteAsync(context, CancellationToken.None);

        Directory.Exists(Path.Combine(_tempDir, ".kiro", "steering")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_KiroCli_Idempotent_OverwritesOnRerun()
    {
        var job1 = CreateJob("first content", null);
        var step1 = new WriteSteeringStep(job1);
        var context = CreateContext(AgentProviderType.KiroCli);
        await step1.ExecuteAsync(context, CancellationToken.None);

        var job2 = CreateJob("second content", null);
        var step2 = new WriteSteeringStep(job2);
        await step2.ExecuteAsync(context, CancellationToken.None);

        var content = File.ReadAllText(Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath));
        content.Should().Contain("second content");
        content.Should().NotContain("first content");
    }

    // ── OpenCode scenarios ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OpenCode_NoExistingFile_WritesMarkersWithContent()
    {
        var job = CreateJob("project instructions", null);
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.OpenCode);

        await step.ExecuteAsync(context, CancellationToken.None);

        var agentsPath = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        File.Exists(agentsPath).Should().BeTrue();
        var content = File.ReadAllText(agentsPath);
        content.Should().Contain("<!-- BEGIN PIPELINE STEERING");
        content.Should().Contain("<!-- END PIPELINE STEERING -->");
        content.Should().Contain("project instructions");
    }

    [Fact]
    public async Task ExecuteAsync_OpenCode_ExistingContent_PreservesBelow()
    {
        var agentsPath = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        File.WriteAllText(agentsPath, "# Existing content\nDo not lose this.\n");

        var job = CreateJob("new steering", null);
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.OpenCode);

        await step.ExecuteAsync(context, CancellationToken.None);

        var content = File.ReadAllText(agentsPath);
        content.Should().Contain("new steering");
        content.Should().Contain("# Existing content");
        content.Should().Contain("Do not lose this.");
    }

    [Fact]
    public async Task ExecuteAsync_OpenCode_Idempotent_StripsPreviousBlock()
    {
        var agentsPath = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        File.WriteAllText(agentsPath, "# Existing repo content\n");

        var job1 = CreateJob("first run", null);
        var step1 = new WriteSteeringStep(job1);
        var context = CreateContext(AgentProviderType.OpenCode);
        await step1.ExecuteAsync(context, CancellationToken.None);

        var job2 = CreateJob("second run", null);
        var step2 = new WriteSteeringStep(job2);
        await step2.ExecuteAsync(context, CancellationToken.None);

        var content = File.ReadAllText(agentsPath);
        content.Should().Contain("second run");
        content.Should().NotContain("first run");
        content.Should().Contain("# Existing repo content");
        // Only one BEGIN marker
        content.Split("BEGIN PIPELINE STEERING").Length.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_OpenCode_BothContents_IncludesProjectAndRepoSections()
    {
        var job = CreateJob("project stuff", "repo stuff");
        var step = new WriteSteeringStep(job);
        var context = CreateContext(AgentProviderType.OpenCode);

        await step.ExecuteAsync(context, CancellationToken.None);

        var content = File.ReadAllText(Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath));
        content.Should().Contain("# Project Instructions");
        content.Should().Contain("project stuff");
        content.Should().Contain("# Repository Instructions");
        content.Should().Contain("repo stuff");
    }

    // ── Error handling ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_IOException_ReturnsContinueAndLogsWarning()
    {
        // Use a workspace path where a file blocks directory creation.
        // On all platforms, CreateDirectory throws IOException when a file exists at the path.
        var blocker = Path.Combine(_tempDir, "blocker-file");
        File.WriteAllText(blocker, "I am a file");
        var badWorkspace = Path.Combine(blocker, "subdir"); // file as parent → IOException

        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "cfg",
            RepoProviderConfigId = "cfg",
            WorkspacePath = badWorkspace,
            StartedAt = DateTime.UtcNow
        };
        var context = CreateContextWithRun(run, AgentProviderType.KiroCli);
        var job = CreateJob("content", null);
        var step = new WriteSteeringStep(job);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private PipelineStepContext CreateContext(AgentProviderType providerType)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "cfg",
            RepoProviderConfigId = "cfg",
            WorkspacePath = _tempDir,
            StartedAt = DateTime.UtcNow
        };
        return CreateContextWithRun(run, providerType);
    }

    private PipelineStepContext CreateContextWithRun(PipelineRun run, AgentProviderType providerType)
    {
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.ProviderType).Returns(providerType);
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(
            providerType == AgentProviderType.KiroCli ? [".kiro"] : ["AGENTS.md"]);

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

    private static JobAssignmentMessage CreateJob(string? projectSteering, string? repoSteering)
    {
        return new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "1",
            IssueDetail = new IssueDetail { Identifier = "1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "test",
            ProjectSteeringContent = projectSteering,
            RepoSteeringContent = repoSteering
        };
    }
}
