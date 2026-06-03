using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="RunEnvironmentSetupStep"/>.
/// Requirements: 3.4, 3.5, 3.9, 3.10, 3.11, 8.4
/// </summary>
public class RunEnvironmentSetupStepTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly List<string> _emittedLines = [];

    public RunEnvironmentSetupStepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"env-setup-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _mockCallbacks
            .Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _emittedLines.Add(line));

        // FailRunAsync calls SwapAgentLabel → mock it to return completed task
        _mockCallbacks
            .Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Null/Empty secrets and steps → returns Continue immediately ──────

    [Fact]
    public async Task ExecuteAsync_NullSecretsAndNullSetupSteps_ReturnsContinueImmediately()
    {
        // Arrange
        var job = CreateJobWithRepoConfig(secrets: null, setupSteps: null);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(c => c.TransitionTo(It.IsAny<PipelineStep>()), Times.Never);
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EmptySecretsAndEmptySetupSteps_ReturnsContinueImmediately()
    {
        // Arrange
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string>(),
            setupSteps: []);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(c => c.TransitionTo(It.IsAny<PipelineStep>()), Times.Never);
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchingRepoConfig_ReturnsContinueImmediately()
    {
        // Arrange — job with RepoProviderConfigId that doesn't match any config
        var job = new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "non-existent-config",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "test-user"
        };
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
    }

    // ── Step with exit code 0 → returns Continue ─────────────────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_StepExitCode0_ReturnsContinue()
    {
        if (!OperatingSystem.IsLinux())
            return; // This test uses /bin/bash

        // Arrange
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string> { ["MY_VAR"] = "test-value" },
            setupSteps: [new SetupStep { Name = "Echo test", Command = "echo hello" }]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.RunningEnvironmentSetup), Times.Once);
        _emittedLines.Should().Contain(l => l.Contains("Environment setup complete"));
    }

    // ── Step with non-zero exit → returns Stop with failure reason ────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_StepNonZeroExit_ReturnsStopWithFailureReason()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange
        var job = CreateJobWithRepoConfig(
            secrets: null,
            setupSteps: [new SetupStep { Name = "Failing step", Command = "exit 42" }]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Stop);
        context.Run.FailureReason.Should().Contain("Failing step");
        context.Run.FailureReason.Should().Contain("42");
    }

    // ── Multiple steps execute in order ──────────────────────────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_MultipleSteps_ExecuteInOrder()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — create a file with step1, append with step2
        var markerFile = Path.Combine(_tempDir, "order-marker.txt");
        var job = CreateJobWithRepoConfig(
            secrets: null,
            setupSteps:
            [
                new SetupStep { Name = "Step 1", Command = $"echo 'first' > {markerFile}" },
                new SetupStep { Name = "Step 2", Command = $"echo 'second' >> {markerFile}" }
            ]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var contents = await File.ReadAllTextAsync(markerFile);
        contents.Should().Contain("first");
        contents.Should().Contain("second");
        // Verify 'first' appears before 'second'
        contents.IndexOf("first").Should().BeLessThan(contents.IndexOf("second"));
    }

    // ── Secrets are injected as environment variables ─────────────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_SecretsInjectedAsEnvironmentVariables()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — use printenv to verify secrets are available
        var outputFile = Path.Combine(_tempDir, "env-output.txt");
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string>
            {
                ["MY_SECRET_TOKEN"] = "super-secret-value-1234",
                ["FEED_URL"] = "https://nuget.example.com/v3/index.json"
            },
            setupSteps: [new SetupStep { Name = "Check env", Command = $"printenv MY_SECRET_TOKEN > {outputFile}" }]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var envValue = (await File.ReadAllTextAsync(outputFile)).Trim();
        envValue.Should().Be("super-secret-value-1234");
    }

    // ── Log masking replaces secret values with *** in output ─────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_LogMaskingReplacesSecretValuesInOutput()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — echo a secret value to stdout, verify it's masked
        var secretValue = "my-secret-password-1234";
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string> { ["PASSWORD"] = secretValue },
            setupSteps: [new SetupStep { Name = "Print secret", Command = $"echo 'The password is {secretValue}'" }]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        // The emitted output should have the secret value replaced with ***
        _emittedLines.Should().NotContain(l => l.Contains(secretValue));
        _emittedLines.Should().Contain(l => l.Contains("***"));
    }

    // ── Secret values shorter than 4 chars are NOT masked ────────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_ShortSecretValuesNotMasked()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — short secret value (3 chars) should NOT be masked
        var shortSecret = "abc";
        var longSecret = "long-secret-value";
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string>
            {
                ["SHORT"] = shortSecret,
                ["LONG"] = longSecret
            },
            setupSteps: [new SetupStep { Name = "Print values", Command = $"echo '{shortSecret} and {longSecret}'" }]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        // Short secret (3 chars) should remain visible in output
        _emittedLines.Should().Contain(l => l.Contains(shortSecret));
        // Long secret should be masked
        _emittedLines.Should().NotContain(l => l.Contains(longSecret));
        _emittedLines.Should().Contain(l => l.Contains("***"));
    }

    // ── Transitions to RunningEnvironmentSetup when steps exist ───────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_WithSteps_TransitionsToRunningEnvironmentSetup()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string> { ["TOKEN"] = "value-longer-than-four" },
            setupSteps: [new SetupStep { Name = "Simple step", Command = "true" }]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.RunningEnvironmentSetup), Times.Once);
    }

    // ── Secrets only (no setup steps) still transitions ──────────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_SecretsOnlyNoSetupSteps_TransitionsAndReturnsContinue()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — has secrets but no setup steps
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string> { ["TOKEN"] = "value-1234" },
            setupSteps: null);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.RunningEnvironmentSetup), Times.Once);
        // With 0 steps, should emit completion message
        _emittedLines.Should().Contain(l => l.Contains("0 steps"));
    }

    // ── First failing step stops execution of subsequent steps ────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_FirstStepFails_SubsequentStepsNotExecuted()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange
        var markerFile = Path.Combine(_tempDir, "should-not-exist.txt");
        var job = CreateJobWithRepoConfig(
            secrets: null,
            setupSteps:
            [
                new SetupStep { Name = "Fail early", Command = "exit 1" },
                new SetupStep { Name = "Should not run", Command = $"touch {markerFile}" }
            ]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Stop);
        File.Exists(markerFile).Should().BeFalse();
    }

    // ── Output Visibility Verification (Req 8.1, 8.2, 8.3) ─────────────

    /// <summary>
    /// Verifies that all expected output lines appear in the pipeline run's output stream
    /// and that the RunningEnvironmentSetup step transition is reported via callbacks (which
    /// propagate to SignalR in production).
    ///
    /// In production, <see cref="IPipelineCallbacks.EmitOutputLine"/> routes through:
    ///   Agent-side: EmitOutputLineInternalAsync → run.OutputLines.Enqueue + OutputBatcher → SignalR "ReportOutputLines"
    ///   Server-side: PipelineRunLifecycleService.EmitOutputLine → run.OutputLines.Enqueue + OnOutputLine event
    ///
    /// And <see cref="IPipelineCallbacks.TransitionTo"/> routes through:
    ///   Agent-side: TransitionToInternalAsync → SignalR "ReportStepTransition"
    ///   Server-side: PipelineRunLifecycleService.TransitionTo → run.CurrentStep + OnChange event
    ///
    /// Requirements: 8.1 (output lines in run stream), 8.2 (failure output with step name + exit code),
    ///               8.3 (RunningEnvironmentSetup transition reported via SignalR)
    /// </summary>
    [Fact]
    [Trait("Platform", "Linux")]
    public async Task OutputVisibility_AllOutputLinesAndTransitionReportedViaCallbacks()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — two named steps so we can verify per-step output lines
        var transitions = new List<PipelineStep>();
        _mockCallbacks
            .Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(s => transitions.Add(s));

        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string> { ["TOKEN"] = "secret-token-value" },
            setupSteps:
            [
                new SetupStep { Name = "Install dependencies", Command = "echo 'installing...'" },
                new SetupStep { Name = "Configure feed", Command = "echo 'configured'" }
            ]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — Step completes
        result.Should().Be(StepResult.Continue);

        // Req 8.3: RunningEnvironmentSetup transition is reported (propagates to SignalR in production)
        transitions.Should().ContainSingle(s => s == PipelineStep.RunningEnvironmentSetup,
            "the step must report RunningEnvironmentSetup via TransitionTo so the UI displays the current step");

        // Req 8.1: Step name output lines appear in the run's output stream
        _emittedLines.Should().Contain(l => l.Contains("Running setup: Install dependencies"),
            "each setup step's name must be emitted before execution (visible in UI run detail)");
        _emittedLines.Should().Contain(l => l.Contains("Running setup: Configure feed"),
            "each setup step's name must be emitted before execution (visible in UI run detail)");

        // Req 8.1: Command stdout/stderr output appears in the run's output stream
        _emittedLines.Should().Contain(l => l.Contains("installing..."),
            "stdout from setup step commands must appear in the pipeline output stream");
        _emittedLines.Should().Contain(l => l.Contains("configured"),
            "stdout from setup step commands must appear in the pipeline output stream");

        // Req 8.1: Completion summary appears in output
        _emittedLines.Should().Contain(l => l.Contains("Environment setup complete (2 steps)"),
            "completion summary must appear in the pipeline output stream");
    }

    /// <summary>
    /// Verifies that failure output includes step name and exit code in the run's output stream
    /// (requirement 8.2: failure message includes step name + exit code + stderr for debugging).
    /// Requirements: 8.2
    /// </summary>
    [Fact]
    [Trait("Platform", "Linux")]
    public async Task OutputVisibility_FailureMessage_IncludesStepNameAndExitCode()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Arrange — step that writes to stderr and exits non-zero
        var job = CreateJobWithRepoConfig(
            secrets: new Dictionary<string, string> { ["DUMMY"] = "dummy-value" },
            setupSteps:
            [
                new SetupStep { Name = "Auth check", Command = "echo 'auth failed: invalid token' >&2; exit 5" }
            ]);
        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — pipeline stops
        result.Should().Be(StepResult.Stop);

        // Req 8.2: Failure message includes step name and exit code for debugging
        context.Run.FailureReason.Should().Contain("Auth check",
            "failure message must include the step name to aid debugging");
        context.Run.FailureReason.Should().Contain("5",
            "failure message must include the exit code to aid debugging");
        context.Run.FailureReason.Should().Contain("auth failed",
            "failure message must include stderr output to aid debugging");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private PipelineStepContext CreateTestContext()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-config-1",
            RepoProviderConfigId = "repo-config-1",
            WorkspacePath = _tempDir,
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
            Callbacks = _mockCallbacks.Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            AgentExecution = new Mock<IAgentPhaseExecutor>().Object,
            QualityGates = new Mock<IQualityGateExecutor>().Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
            Logger = _mockLogger.Object
        };
    }

    private static JobAssignmentMessage CreateJobWithRepoConfig(
        Dictionary<string, string>? secrets,
        IReadOnlyList<SetupStep>? setupSteps)
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test/repo",
            Settings = new Dictionary<string, string>(),
            Secrets = secrets,
            SetupSteps = setupSteps
        };

        return new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            InitiatedBy = "test-user"
        };
    }
}
