using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Tests for the orchestrator-side <see cref="RunEnvironmentSetupStep"/>.
/// Targets <c>src/CodingAgent.Pipeline/Services/Steps/RunEnvironmentSetupStep.cs</c> — NOT
/// the same-named class in CodingAgent.Agent (which takes AgentJobState in its constructor).
///
/// Tests (a), (b), (c) spawn real shell processes because <see cref="SetupCommandRunner"/> is
/// a static class with no injection seam. They are NOT tagged Integration so that they run
/// during the SonarQube coverage phase (CI filters out the Integration category).
/// </summary>
public class RunEnvironmentSetupStepTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _activities = [];
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();

    public RunEnvironmentSetupStepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"env-setup-pipeline-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // All four Callbacks methods called by FailRunAsync must be set up explicitly.
        // On loose Moq, AddRunToHistoryAsync returns null for Task; awaiting null throws
        // NullReferenceException inside FailRunAsync.
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks
            .Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        // Register an ActivityListener so PipelineTelemetry.ActivitySource.StartActivity(...)
        // returns a real Activity and sets Activity.Current. This makes Activity.Current?.SetStatus(...)
        // inside the step's failure branches observable via the _activities bag.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PipelineStepContext BuildContext(string workspacePath, ProviderConfig? repoConfig)
    {
        _configStore
            .Setup(s => s.GetProviderConfigByIdAsync(
                It.IsAny<string>(), ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        return new PipelineStepContext
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "42",
                IssueTitle = "Test issue",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = PipelineRunType.Implementation,
                WorkspacePath = workspacePath
            },
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Criterion (a): repoConfig has secrets and one setup step that succeeds.
    /// TransitionTo(RunningEnvironmentSetup) called, EmitOutputLine contains the step count,
    /// StepResult.Continue returned.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithSecretsAndOneSuccessfulStep_TransitionsAndReturnsContinue()
    {
        if (!OperatingSystem.IsLinux())
            return; // bash-based step commands (echo via /bin/bash -c) require Linux

        // Arrange
        var repoConfig = new ProviderConfig
        {
            DisplayName = "test-repo",
            Kind = ProviderKind.Repository,
            ProviderType = "github",
            Secrets = new Dictionary<string, string> { ["MY_KEY"] = "secret-value" },
            SetupSteps = [new SetupStep { Command = "echo hello", Name = "greet-step" }]
        };
        var context = BuildContext(_tempDir, repoConfig);
        var step = new RunEnvironmentSetupStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue, "all setup steps succeeded");
        _callbacks.Verify(
            c => c.TransitionTo(PipelineStep.RunningEnvironmentSetup),
            Times.Once,
            "step must signal the pipeline is running environment setup");
        _callbacks.Verify(
            c => c.EmitOutputLine(It.Is<string>(s => s.Contains("complete (1 steps)"))),
            Times.Once,
            "success banner must include the exact count of steps that ran");
    }

    /// <summary>
    /// Criterion (b): a setup step result has a non-null Exception.
    /// FailRunAsync called (observable via SwapAgentLabel to agent:error), StepResult.Stop returned.
    /// Supplying a non-existent working directory causes SetupCommandRunner to catch the
    /// DirectoryNotFoundException from Process.Start and return result.Exception != null.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SetupStepThrowsException_CallsFailRunAsyncAndReturnsStop()
    {
        if (!OperatingSystem.IsLinux())
            return; // Process.Start with a non-existent working directory throws on Linux; behaviour is platform-specific

        // Arrange — the directory must not exist so process start throws
        var nonExistentDir = Path.Combine(
            Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"));
        var repoConfig = new ProviderConfig
        {
            DisplayName = "test-repo",
            Kind = ProviderKind.Repository,
            ProviderType = "github",
            Secrets = new Dictionary<string, string> { ["MY_KEY"] = "secret-value" },
            SetupSteps = [new SetupStep { Command = "echo hello", Name = "step-1" }]
        };
        var context = BuildContext(nonExistentDir, repoConfig);
        var step = new RunEnvironmentSetupStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — exception path: RecordMaskedError sets ActivityStatusCode.Error
        var setupActivity = _activities.FirstOrDefault(a => a.DisplayName == "RunEnvironmentSetup");
        setupActivity.Should().NotBeNull("the ActivityListener must capture the RunEnvironmentSetup span");
        setupActivity!.Status.Should().Be(ActivityStatusCode.Error,
            "RecordMaskedError calls activity.SetStatus(Error) when result.Exception is non-null");

        // FailRunAsync swaps the label to agent:error
        result.Should().Be(StepResult.Stop,
            "a non-null Exception on the step result must stop the pipeline");
        _callbacks.Verify(
            c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once,
            "FailRunAsync must be called when the step result has a non-null Exception");
    }

    /// <summary>
    /// Criterion (c): a setup step result has a null Exception and FailureMessage containing 'timed out'.
    /// Activity.SetStatus(Error) called, StepResult.Stop returned.
    ///
    /// Uses a command that writes "timed out" to stderr and exits non-zero. SetupCommandRunner
    /// includes stderr in FailureMessage for exit-code failures, so Exception is null and
    /// FailureMessage.Contains("timed out") is true — no 120-second wait required.
    /// The ActivityListener registered in the constructor enables sampling so Activity.Current
    /// is non-null when the step runs, making the SetStatus call observable.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_StepFailsWithTimedOutMessage_SetsActivityErrorStatusAndReturnsStop()
    {
        if (!OperatingSystem.IsLinux())
            return; // bash-based stderr redirection (>&2) requires /bin/bash on Linux

        // Arrange — exit 1 + "timed out" on stderr → FailureMessage.Contains("timed out"), Exception == null
        //
        // This test exercises the `else if (result.FailureMessage?.Contains("timed out") == true)` branch
        // by injecting "timed out" into stderr. SetupCommandRunner embeds truncatedStderr in the exit-code
        // FailureMessage, which makes Contains("timed out") true without waiting for the 120-second timeout.
        //
        // TODO: RunEnvironmentSetupStep.cs line 62 has a TODO to replace string-matching with a structured
        // FailureKind/IsTimeout property on SetupCommandResult. If that refactor removes stderr from
        // FailureMessage, this test will fail at the activity status assertion (Status == Unset) rather than
        // at the StepResult assertion, pointing to the broken branch rather than to this test.
        var repoConfig = new ProviderConfig
        {
            DisplayName = "test-repo",
            Kind = ProviderKind.Repository,
            ProviderType = "github",
            Secrets = new Dictionary<string, string> { ["MY_KEY"] = "secret-value" },
            SetupSteps =
            [
                new SetupStep
                {
                    Command = "echo 'timed out' >&2; exit 1",
                    Name = "timeout-step"
                }
            ]
        };
        var context = BuildContext(_tempDir, repoConfig);
        var step = new RunEnvironmentSetupStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — activity must record Error status via Activity.Current?.SetStatus(...)
        var setupActivity = _activities.FirstOrDefault(a => a.DisplayName == "RunEnvironmentSetup");
        setupActivity.Should().NotBeNull(
            "the ActivityListener must capture the RunEnvironmentSetup span");
        setupActivity!.Status.Should().Be(ActivityStatusCode.Error,
            "a FailureMessage containing 'timed out' must set Error status on the activity");

        result.Should().Be(StepResult.Stop,
            "a timed-out failure must stop the pipeline");
        _callbacks.Verify(
            c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once,
            "FailRunAsync must be called after setting the activity error status");
    }

    /// <summary>
    /// Criterion (d): non-Linux guard skips execution and returns Continue.
    /// OperatingSystem.IsLinux() is a non-mockable platform intrinsic that always returns true
    /// on Linux CI — this path cannot be exercised on the standard build host.
    /// </summary>
    [Fact(Skip = "Non-Linux only")]
    public async Task ExecuteAsync_NonLinuxPlatform_SkipsExecutionAndReturnsContinue()
    {
        // Arrange — repoConfig with steps would normally trigger execution,
        // but the non-Linux guard returns before reaching the config store
        var context = BuildContext(_tempDir, repoConfig: null);
        var step = new RunEnvironmentSetupStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue,
            "non-Linux orchestrators must skip setup and return Continue");
        _callbacks.Verify(
            c => c.TransitionTo(It.IsAny<PipelineStep>()),
            Times.Never,
            "setup must not transition state when skipped on non-Linux");
        _callbacks.Verify(
            c => c.EmitOutputLine(It.IsAny<string>()),
            Times.Never,
            "setup must not emit output when skipped on non-Linux");
        _configStore.Verify(
            s => s.GetProviderConfigByIdAsync(
                It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the config store must not be queried when the non-Linux guard fires");
    }
}
