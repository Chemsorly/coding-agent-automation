using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Tests for <see cref="DecompositionAnalysisStep"/> verifying that the failure reason message
/// distinguishes between "agent ran but produced no file" and "agent could not run because epic
/// context was unavailable" (acceptance criterion 3 of issue #2601).
///
/// Uses the same test infrastructure as <see cref="DecompositionAnalysisStepTokenAccumulationTests"/>
/// (real temp workspace, Mock IAgentProvider, Mock IAgentIssueOperations, Mock IPipelineCallbacks).
///
/// Assertion target: <c>run.FailureReason</c> — <see cref="PipelineStepContext.FailRunAsync"/>
/// sets this property directly before calling callbacks, making it the authoritative record
/// of the failure reason string without needing any mock Verify.
/// </summary>
public class DecompositionAnalysisStepFailureReasonTests : IDisposable
{
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();

    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly string _workspacePath;
    private readonly string _agentDir;

    public DecompositionAnalysisStepFailureReasonTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"decomp-failreason-test-{Guid.NewGuid():N}");
        _agentDir = Path.Combine(_workspacePath, ".agent");
        Directory.CreateDirectory(_agentDir);

        // Default callback setup — all methods must be set up to avoid null-return issues
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        // TODO: PipelineStepContext.FailRunAsync calls EmitOutputLine internally (the "❌ Pipeline failed: ..."
        // line) in addition to the EmitOutputLine call in DecompositionAnalysisStep itself. The current
        // MockBehavior.Loose means this does not cause failures, but a future switch to MockBehavior.Strict
        // would break tests here because the internal FailRunAsync call is not explicitly stubbed.
        // If strict mocks are ever adopted, add: _callbacks.Setup(c => c.EmitOutputLine(It.Is<string>(s => s.StartsWith("❌ Pipeline failed"))));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.NotifyChange());
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        // Default health status — process alive, not stalling
        _agentProvider
            .Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, IsProcessAlive = true });
        _agentProvider
            .Setup(p => p.GetLatestSessionIdAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-abc");
        _agentProvider.Setup(p => p.ProviderType).Returns(AgentProviderType.KiroCli);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.DecompositionAnalysis,
        ProjectId = "decomp-test",
        ProjectName = "TestProject",
        WorkspacePath = _workspacePath
    };

    private PipelineStepContext BuildContext(PipelineRun run) =>
        new()
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                AgentTimeout = TimeSpan.FromMinutes(30),
                DecompositionTimeout = TimeSpan.FromMinutes(15),
                StallPollInterval = TimeSpan.FromSeconds(30),
                StallWarningInterval = TimeSpan.FromMinutes(2),
                MaxDecompositionSubIssues = 10,
                MaxDecompositionSubIssueFiles = 12,
                OutputLinesCapacity = 500,
                ChatHistoryCapacity = 50,
                QualityGateHistoryCapacity = 10,
                RetryErrorsCapacity = 10,
                BlacklistedPaths = [".agent"],
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = _agentProvider.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger
        };

    /// <summary>
    /// Sets up the agent provider to return success (exit code 0) without writing the plan file.
    /// This simulates the agent running but producing no output.
    /// </summary>
    private void SetupAgentSuccessNoPlanFile()
    {
        _agentProvider
            .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Agent completed"],
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });
    }

    // ── AC3: Failure reason distinguishes context-unavailable from no-output ──

    /// <summary>
    /// AC3 (issue #2601): When WriteEpicContextAsync fails (GetIssueAsync throws)
    /// AND the agent does not produce a plan file, the run failure reason must mention
    /// that context was unavailable — not the generic "Agent did not produce" message.
    ///
    /// Assertion: run.FailureReason is set directly by PipelineStepContext.FailRunAsync.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenEpicContextFails_PlanMissing_FailureReasonMentionsContextUnavailable()
    {
        // Arrange — GetIssueAsync throws to simulate a hub/RequestGetIssue failure
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HubException: Failed to invoke 'RequestGetIssue'"));
        // ListCommentsAsync will not be reached because GetIssueAsync throws first,
        // but set it up for completeness
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        SetupAgentSuccessNoPlanFile();

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Stop,
            "the step must stop when the plan file is missing");

        run.FailureReason.Should().Contain("context was unavailable",
            "when WriteEpicContextAsync fails, the failure reason must distinguish this from a normal agent no-output case");
        run.FailureReason.Should().Contain("RequestGetIssue failed",
            "the failure reason must hint at the underlying hub operation that failed");
    }

    /// <summary>
    /// AC2 (issue #2729): When WriteEpicContextAsync fails (GetIssueAsync throws) AND the agent
    /// does not produce a plan file, the run FailureCategory must be set to
    /// <see cref="FailureReason.InfrastructureFailure"/>.
    ///
    /// This ensures the DB column WorkItems.FailureReason receives InfrastructureFailure
    /// (not the default AgentError) — distinguishing infrastructure/context failures from
    /// ordinary missing-output failures in telemetry and operator tooling.
    ///
    /// This test FAILS before the fix (FailureCategory is null / default) and PASSES after.
    /// </summary>
    // TODO (WARNING — TestQuality): This test has identical Arrange setup to
    // ExecuteAsync_WhenEpicContextFails_PlanMissing_FailureReasonMentionsContextUnavailable —
    // same mocks, same GetIssueAsync throw, same SetupAgentSuccessNoPlanFile, same run/context.
    // Only the final assertion differs (FailureCategory vs FailureReason string).
    // Consider merging both tests into a single parameterised or combined test that asserts
    // both FailureReason and FailureCategory in one Act, reducing the duplicated Arrange block
    // that must be kept in sync if the shared setup changes.
    [Fact]
    public async Task ExecuteAsync_WhenEpicContextFails_PlanMissing_FailureCategoryIsInfrastructureFailure()
    {
        // Arrange — same setup as FailureReasonMentionsContextUnavailable
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HubException: Failed to invoke 'RequestGetIssue'"));
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        SetupAgentSuccessNoPlanFile();

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Stop,
            "the step must stop when the plan file is missing");

        run.FailureCategory.Should().Be(FailureReason.InfrastructureFailure,
            "epicContextFailed path must classify the failure as InfrastructureFailure so operators " +
            "can distinguish it from ordinary agent failures in WorkItems.FailureReason");
    }

    /// <summary>
    /// AC3 (issue #2601) regression guard: When WriteEpicContextAsync succeeds AND issues were
    /// successfully downloaded (OpenIssuesDownloaded > 0) but the agent does not produce a plan
    /// file, the failure reason must use the original generic message — NOT the context-unavailable
    /// message.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenEpicContextSucceeds_PlanMissing_FailureReasonMentionsAgentDidNotProduce()
    {
        // Arrange — issue ops succeed (epic context writes normally)
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Epic",
                Description = "Epic description",
                Labels = Array.Empty<string>()
            });
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        SetupAgentSuccessNoPlanFile();

        var run = CreateRun();
        // Signal that open-issue context was fully downloaded — this distinguishes "epic write
        // succeeded, agent just produced no output" from the degraded WriteOpenIssueContext path
        // (OpenIssuesDownloaded == 0 would trigger epicContextFailed for decomposition runs).
        run.OpenIssuesDownloaded = 5;
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Stop,
            "the step must stop when the plan file is missing");

        run.FailureReason.Should().Contain("Agent did not produce",
            "when epic context succeeded, the failure reason must use the generic missing-output message");
        run.FailureReason.Should().NotContain("context was unavailable",
            "the context-unavailable message must not appear when WriteEpicContextAsync succeeded");
    }

    // ── IsOpenIssueContextDegraded path ───────────────────────────────────────

    /// <summary>
    /// When WriteEpicContextAsync succeeds but WriteOpenIssueContext wrote 0 files
    /// (all RequestGetIssue calls silently failed), epicContextFailed must be set true
    /// and the run must use FailureReason.InfrastructureFailure.
    /// Covers the IsOpenIssueContextDegraded branch (lines 97–107 of DecompositionAnalysisStep).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenOpenIssueContextDegraded_PlanMissing_FailureCategoryIsInfrastructureFailure()
    {
        // Arrange — epic context succeeds but OpenIssuesDownloaded == 0 (hub errors during WriteOpenIssueContext)
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Epic", Description = "desc", Labels = Array.Empty<string>() });
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        SetupAgentSuccessNoPlanFile();

        var run = CreateRun();
        // OpenIssuesDownloaded stays at 0 (default) — simulates all RequestGetIssue calls silently failing
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        run.FailureCategory.Should().Be(FailureReason.InfrastructureFailure,
            "degraded WriteOpenIssueContext (0 files written) must be treated as InfrastructureFailure");
        run.FailureReason.Should().Contain("context was unavailable");
    }

    // ── Agent non-zero exit code path ─────────────────────────────────────────

    /// <summary>
    /// When the agent exits with a non-zero exit code, the step must fail the run with
    /// FailureReason.ExitCodeFailure and include the exit code in the reason string.
    /// Covers lines 140–145 of DecompositionAnalysisStep.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenAgentExitsNonZero_FailureCategoryIsExitCodeFailure()
    {
        // Arrange — epic context and issue download succeed; agent returns exit code 5
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Epic", Description = "desc", Labels = Array.Empty<string>() });
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        _agentProvider
            .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 5, OutputLines = ["error output"], Usage = new TokenUsage() });

        var run = CreateRun();
        run.OpenIssuesDownloaded = 3;
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        run.FailureCategory.Should().Be(FailureReason.ExitCodeFailure);
        run.FailureReason.Should().Contain("5", "exit code 5 must appear in the failure reason");
        run.FailureReason.Should().Contain("non-zero exit code");
    }

    // ── Plan file too short path ───────────────────────────────────────────────

    /// <summary>
    /// When the agent produces a plan file but its content is below MinimumContentThreshold (20 chars),
    /// the step must stop and report the short-content failure reason.
    /// Covers lines 179–184 of DecompositionAnalysisStep.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenPlanFileTooShort_StopsWithTooShortMessage()
    {
        // Arrange — epic context succeeds, agent succeeds and writes a plan file that is too short
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "42", Title = "Epic", Description = "desc", Labels = Array.Empty<string>() });
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        _agentProvider
            .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["done"], Usage = new TokenUsage() });

        // Write a plan file with only 5 characters — below the 20-char MinimumContentThreshold
        var planPath = Path.Combine(_agentDir, "decomposition-plan.md");
        await File.WriteAllTextAsync(planPath, "short");

        var run = CreateRun();
        run.OpenIssuesDownloaded = 3;
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        run.FailureReason.Should().Contain("too short",
            "the plan file content is below MinimumContentThreshold and must be reported as too short");
    }
}
