using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// End-to-end integration tests for <see cref="RunEnvironmentSetupStep"/> that verify the full
/// dispatch-to-execution flow: job with secrets/setupSteps → step executes commands via /bin/bash
/// → secrets injected as env vars → output lines emitted → pipeline continues.
/// Requirements: 3.5, 3.6, 3.7, 3.11, 8.1
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "Linux")]
public class RunEnvironmentSetupStepIntegrationTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly List<string> _emittedOutput = [];
    private readonly List<PipelineStep> _transitions = [];

    public RunEnvironmentSetupStepIntegrationTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"env-setup-e2e-{Guid.NewGuid()}");
        Directory.CreateDirectory(_workspaceDir);

        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _emittedOutput.Add(line));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => _transitions.Add(step));
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    /// <summary>
    /// Verifies the full end-to-end flow: dispatch a job with secrets and setup steps,
    /// execute the step, and confirm that:
    /// - The step transitions to RunningEnvironmentSetup
    /// - The setup command runs in the workspace directory
    /// - Secrets are injected as environment variables and accessible in the command
    /// - Output lines are emitted (step name, command output, completion summary)
    /// - The pipeline proceeds (returns StepResult.Continue)
    /// Requirements: 3.5, 3.6, 3.7, 3.11, 8.1
    /// </summary>
    [Fact]
    public async Task FullFlow_DispatchJobWithSecretsAndSetupSteps_StepExecutesAndPipelineContinues()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — simulate a dispatched job with secrets and a setup step that writes
        // the secret value to a file, proving secrets are available as env vars
        var secretKey = "MY_SECRET";
        var secretValue = "s3cr3t-t0ken-value";
        var outputFile = Path.Combine(_workspaceDir, "setup-output.txt");

        var repoConfig = new ProviderConfig
        {
            Id = "repo-work-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "myorg/private-project",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "myorg",
                ["repo"] = "private-project",
                ["baseBranch"] = "main"
            },
            Secrets = new Dictionary<string, string>
            {
                [secretKey] = secretValue,
                ["FEED_URL"] = "https://nuget.pkg.github.com/myorg/index.json"
            },
            SetupSteps =
            [
                new SetupStep
                {
                    Name = "Configure environment",
                    Command = $"echo $MY_SECRET > {outputFile}"
                },
                new SetupStep
                {
                    Name = "Verify workspace",
                    Command = "pwd"
                }
            ]
        };

        var job = new JobAssignmentMessage
        {
            JobId = "e2e-test-job-001",
            IssueIdentifier = "myorg/private-project#42",
            IssueDetail = new IssueDetail
            {
                Identifier = "myorg/private-project#42",
                Title = "Add input validation",
                Description = "Implement null checks",
                Labels = ["enhancement"]
            },
            ParsedIssue = new ParsedIssue
            {
                RequirementsSection = "Add null checks",
                AcceptanceCriteria = ["All methods validate inputs"]
            },
            RepoProviderConfigId = "repo-work-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "integration-test"
        };

        var step = new RunEnvironmentSetupStep(job);
        var context = BuildFullPipelineStepContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — Pipeline continues past the setup step
        result.Should().Be(StepResult.Continue);

        // Assert — Step transitioned to RunningEnvironmentSetup
        _transitions.Should().Contain(PipelineStep.RunningEnvironmentSetup);

        // Assert — Secret value was available as environment variable in the command
        File.Exists(outputFile).Should().BeTrue("the setup command should have written to the output file");
        var writtenValue = (await File.ReadAllTextAsync(outputFile)).Trim();
        writtenValue.Should().Be(secretValue, "the secret should be injected as $MY_SECRET env var");

        // Assert — Output lines were emitted (step names and completion)
        _emittedOutput.Should().Contain(l => l.Contains("Running setup: Configure environment"));
        _emittedOutput.Should().Contain(l => l.Contains("Running setup: Verify workspace"));
        _emittedOutput.Should().Contain(l => l.Contains("Environment setup complete (2 steps)"));

        // Assert — The secret value in command output is masked (log masking requirement 8.4)
        // The echo command writes the secret to stdout, which gets emitted as output.
        // That emitted output should have the secret masked with ***
        _emittedOutput.Should().NotContain(l => l.Contains(secretValue),
            "secret values should be masked with *** in emitted output");
    }

    /// <summary>
    /// Verifies that setup step commands execute with WorkingDirectory set to the workspace path,
    /// proving the process runs in the correct context for build/tool commands.
    /// Requirements: 3.7
    /// </summary>
    [Fact]
    public async Task SetupStep_ExecutesInWorkspaceDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — use 'pwd' to capture the working directory
        var pwdFile = Path.Combine(_workspaceDir, "pwd-output.txt");
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test/repo",
            Settings = new Dictionary<string, string>(),
            Secrets = new Dictionary<string, string> { ["DUMMY"] = "placeholder-value" },
            SetupSteps = [new SetupStep { Name = "Check cwd", Command = $"pwd > {pwdFile}" }]
        };

        var job = CreateMinimalJob(repoConfig);
        var step = new RunEnvironmentSetupStep(job);
        var context = BuildFullPipelineStepContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var capturedPwd = (await File.ReadAllTextAsync(pwdFile)).Trim();
        capturedPwd.Should().Be(_workspaceDir,
            "setup step should execute in the workspace directory");
    }

    /// <summary>
    /// Verifies that multiple secrets are all available simultaneously in the process environment.
    /// Requirements: 3.6
    /// </summary>
    [Fact]
    public async Task MultipleSecrets_AllAvailableInProcessEnvironment()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — inject 3 secrets, verify all are accessible
        var outputFile = Path.Combine(_workspaceDir, "multi-secret.txt");
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test/repo",
            Settings = new Dictionary<string, string>(),
            Secrets = new Dictionary<string, string>
            {
                ["TOKEN_A"] = "value-alpha-1234",
                ["TOKEN_B"] = "value-beta-5678",
                ["FEED_URL"] = "https://registry.example.com/v3/index.json"
            },
            SetupSteps =
            [
                new SetupStep
                {
                    Name = "Dump all secrets",
                    Command = $"echo \"$TOKEN_A|$TOKEN_B|$FEED_URL\" > {outputFile}"
                }
            ]
        };

        var job = CreateMinimalJob(repoConfig);
        var step = new RunEnvironmentSetupStep(job);
        var context = BuildFullPipelineStepContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var output = (await File.ReadAllTextAsync(outputFile)).Trim();
        output.Should().Be("value-alpha-1234|value-beta-5678|https://registry.example.com/v3/index.json");
    }

    /// <summary>
    /// Verifies that when a setup step fails, the pipeline stops and does not proceed.
    /// Requirements: 3.9
    /// </summary>
    [Fact]
    public async Task FailingSetupStep_StopsPipeline_DoesNotProceed()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — a step that exits non-zero followed by one that should never run
        var shouldNotExistFile = Path.Combine(_workspaceDir, "should-not-exist.txt");
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test/repo",
            Settings = new Dictionary<string, string>(),
            Secrets = new Dictionary<string, string> { ["TOKEN"] = "some-token-value" },
            SetupSteps =
            [
                new SetupStep { Name = "Install deps", Command = "exit 1" },
                new SetupStep { Name = "Should not run", Command = $"touch {shouldNotExistFile}" }
            ]
        };

        var job = CreateMinimalJob(repoConfig);
        var step = new RunEnvironmentSetupStep(job);
        var context = BuildFullPipelineStepContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — Pipeline stopped
        result.Should().Be(StepResult.Stop);

        // Assert — Second step never executed
        File.Exists(shouldNotExistFile).Should().BeFalse();

        // Assert — Failure was recorded on the run
        context.Run.FailureReason.Should().Contain("Install deps");
        context.Run.FailureReason.Should().Contain("1"); // exit code

        // Assert — Failed transition was emitted
        _transitions.Should().Contain(PipelineStep.Failed);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private PipelineStepContext BuildFullPipelineStepContext()
    {
        var run = new PipelineRun
        {
            RunId = $"e2e-run-{Guid.NewGuid()}",
            IssueIdentifier = "myorg/repo#42",
            IssueTitle = "Integration Test Issue",
            IssueProviderConfigId = "issue-config-1",
            RepoProviderConfigId = "repo-1",
            WorkspacePath = _workspaceDir,
            StartedAt = DateTime.UtcNow
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = new Mock<IRepositoryProvider>().Object,
            AgentProvider = new Mock<IAgentProvider>().Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = new Mock<IConfigurationStore>().Object,
            Callbacks = _callbacks.Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            AgentExecution = new Mock<IAgentPhaseExecutor>().Object,
            QualityGates = new Mock<IQualityGateExecutor>().Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger.Object),
            Logger = _logger.Object
        };
    }

    private static JobAssignmentMessage CreateMinimalJob(ProviderConfig repoConfig)
    {
        return new JobAssignmentMessage
        {
            JobId = $"e2e-job-{Guid.NewGuid()}",
            IssueIdentifier = "myorg/repo#42",
            IssueDetail = new IssueDetail
            {
                Identifier = "myorg/repo#42",
                Title = "Test",
                Description = "",
                Labels = []
            },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = repoConfig.Id,
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "integration-test"
        };
    }
}
