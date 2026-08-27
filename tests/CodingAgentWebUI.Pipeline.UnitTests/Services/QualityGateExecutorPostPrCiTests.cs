using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that the pipeline waits for external CI after finalizing the pull request
/// as ready-for-review. Regression tests for the bug in run 563d3745: the cleanup
/// skipCiIfNoChanges path skipped CI, then FinalizePullRequest triggered PR-event CI,
/// but the pipeline completed without waiting for it.
/// </summary>
public class QualityGateExecutorPostPrCiTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    private static readonly PipelineRunStatus CiPassed = new()
    {
        State = PipelineRunState.Passed,
        Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Passed } }
    };

    private static readonly PipelineRunStatus CiFailed = new()
    {
        State = PipelineRunState.Failed,
        Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Failed, FailureReason = "Job 'build' failed" } }
    };

    public QualityGateExecutorPostPrCiTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-post-pr-ci",
            IssueIdentifier = "2106",
            IssueTitle = "Post-PR CI wait test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-postpr-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-2106-test"
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        SetupDefaultMocks();
    }

    // ── Core regression: post-PR CI is waited on ──────────────────────────────

    /// <summary>
    /// Regression test for run 563d3745: when skipCiIfNoChanges skips CI and
    /// FinalizePullRequest promotes the PR to ready-for-review, the pipeline must
    /// still wait for CI on the PR commit before considering the run complete.
    /// The assertion checks that WaitForCompletionAsync is called AFTER FinalizePullRequest
    /// by verifying the total count exceeds 1 (the initial pre-PR CI is call #1;
    /// the post-PR CI wait must be call #2 or later).
    /// </summary>
    [Fact]
    public async Task WhenSkipCiIfNoChanges_AndPrPromotedToReady_WaitsForCiAfterPrCreation()
    {
        // Arrange: local gates pass, initial commit succeeds (pre-PR CI runs once),
        // then cleanup commit throws "No changes" — skipCiIfNoChanges skips pre-PR CI on cleanup.
        // The post-PR CI wait (the fix) must fire after FinalizePullRequest.
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        var callOrder = new List<string>();

        _mockCallbacks
            .Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("FinalizePullRequest"))
            .Returns(Task.CompletedTask);

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, TimeSpan, CancellationToken>((_, _, _, _) => callOrder.Add("WaitForCompletion"))
            .ReturnsAsync(CiPassed);

        // Act
        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // Assert: FinalizePullRequest must happen before the post-PR WaitForCompletion call
        callOrder.Should().Contain("FinalizePullRequest", "FinalizePullRequest must be called");
        callOrder.Should().Contain("WaitForCompletion", "CI must be polled");

        var finalizeIndex = callOrder.LastIndexOf("FinalizePullRequest");
        var lastCiIndex = callOrder.LastIndexOf("WaitForCompletion");
        lastCiIndex.Should().BeGreaterThan(finalizeIndex,
            "the post-PR CI wait must occur AFTER FinalizePullRequest promotes the PR to ready-for-review");
    }

    /// <summary>
    /// When post-PR CI passes, the run must be finalized as non-draft (Completed), not draft.
    /// </summary>
    [Fact]
    public async Task WhenSkipCiIfNoChanges_AndPostPrCiPasses_FinalizesPrAsNonDraft()
    {
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CiPassed);

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // FinalizePullRequest must be called with isDraft=false exactly once (non-draft = success path)
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "run must complete as non-draft when post-PR CI passes");

        // Must NOT finalize as draft
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()),
            Times.Never,
            "run must not be finalized as draft when post-PR CI passes");
    }

    /// <summary>
    /// When post-PR CI fails, the PR must be converted to a draft (failure path) so the
    /// CI failure is surfaced rather than silently completing the run with a broken PR.
    /// </summary>
    [Fact]
    public async Task WhenSkipCiIfNoChanges_AndPostPrCiFails_FinalizesPrAsDraft()
    {
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        // CI starts but fails
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CiFailed);

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // On CI failure the run must be demoted to draft
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "run must be finalized as draft when post-PR CI fails");
    }

    /// <summary>
    /// When no PipelineProvider is configured, the post-PR CI wait must be skipped entirely
    /// (preserving existing behavior for repos without CI integration).
    /// </summary>
    [Fact]
    public async Task WhenNoPipelineProvider_PostPrCiWaitIsSkipped_AndRunCompletesNormally()
    {
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        await _executor.ProceedToQualityGatesAsync(BuildContext(useRealProvider: false), CancellationToken.None);

        // No CI polling should happen
        _mockPipelineProvider.Verify(
            p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no CI polling when PipelineProvider is null");

        // Run must still finalize as non-draft
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Even when AppendExternalCiIfNeededAsync already ran CI (changes existed during cleanup),
    /// a further post-PR CI wait must still happen after PR promotion. The fix verifies that
    /// WaitForCompletionAsync is called at least once AFTER FinalizePullRequest.
    /// </summary>
    [Fact]
    public async Task WhenCleanupHadChanges_AndPrePrCiAlreadyPassed_StillWaitsForPostPrCi()
    {
        SetupValidatorAlwaysPasses();
        SetupChangesToCommit();

        var callOrder = new List<string>();

        _mockCallbacks
            .Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("FinalizePullRequest"))
            .Returns(Task.CompletedTask);

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, TimeSpan, CancellationToken>((_, _, _, _) => callOrder.Add("WaitForCompletion"))
            .ReturnsAsync(CiPassed);

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // FinalizePullRequest must be followed by at least one more WaitForCompletion (the post-PR CI wait)
        var finalizeIndex = callOrder.LastIndexOf("FinalizePullRequest");
        var lastCiIndex = callOrder.LastIndexOf("WaitForCompletion");

        finalizeIndex.Should().BeGreaterThanOrEqualTo(0, "FinalizePullRequest must be called");
        lastCiIndex.Should().BeGreaterThan(finalizeIndex,
            "CI must be polled AFTER FinalizePullRequest even when pre-PR CI already ran");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        // Default: commits succeed, push succeeds, SHA is readable
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-post-pr-abc");

        // Callback stubs
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Cleanup complete"],
                Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
            });
    }

    private void SetupValidatorAlwaysPasses()
    {
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(PassingReport);
    }

    /// <summary>
    /// Simulates the skipCiIfNoChanges scenario: the initial quality-gate commit
    /// succeeds (branch has work to commit), but the cleanup-pass commit throws
    /// "No changes to commit" — triggering the skipCiIfNoChanges early-exit path
    /// in AppendExternalCiIfNeededAsync.
    /// </summary>
    private void SetupNoChangesToCommit()
    {
        // First CommitAllAsync call (initial QG pass): succeeds — branch has the implementation commit
        // Second CommitAllAsync call (cleanup QG pass with skipCiIfNoChanges=true): no changes
        // Any subsequent calls (e.g. empty-commit retries): succeed
        _mockRepoProvider.SetupSequence(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>)    // initial QG pass — commits ok
            .ThrowsAsync(new InvalidOperationException("No changes to commit")); // cleanup pass — skip CI
    }

    /// <summary>
    /// Simulates cleanup agent making changes — normal commit+push+CI path runs pre-PR.
    /// </summary>
    private void SetupChangesToCommit()
    {
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
    }

    private QualityGateContext BuildContext(IPipelineProvider? pipelineProvider = null, bool useRealProvider = true) => new()
    {
        Run = _run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = 0,
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(50), // prevent 30s default delay in tests
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        },
        AgentProvider = _mockAgent.Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = useRealProvider ? (pipelineProvider ?? _mockPipelineProvider.Object) : null,
        QualityGateConfigs = new[]
        {
            new QualityGateConfiguration
            {
                DisplayName = "Test QGC",
                CompilationCommand = "dotnet",
                CompilationArguments = new[] { "build" },
                TestCommand = "dotnet",
                TestArguments = new[] { "test" }
            }
        },
        Issue = new IssueDetail
        {
            Identifier = "2106",
            Title = "Post-PR CI wait test",
            Description = "Test description",
            Labels = new[] { "bug" }
        }
    };
}

/// <summary>
/// Additional edge-case tests for QualityGateExecutor covering:
/// - WaitForPostPrCiAsync exception/timeout paths
/// - RunPostRetryCleanupAndFinalizeAsync cleanup agent throws
/// - RunRetryLoopAsync empty-session detection
/// </summary>
public class QualityGateExecutorEdgeCaseTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "ok" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" }
    };

    public QualityGateExecutorEdgeCaseTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "edge-case-run",
            IssueIdentifier = "edge#1",
            IssueTitle = "Edge case",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-edge-{Guid.NewGuid():N}"),
            BranchName = "feature/edge-case"
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        SetupDefaultMocks();
    }

    /// <summary>
    /// WaitForPostPrCiAsync: when PollAndHandleInfraRetryAsync throws an OCE from
    /// an inner timeout (not the outer ct), ciGate must be set to Failed and the
    /// run must still be finalized (not left hanging).
    /// </summary>
    [Fact]
    public async Task WaitForPostPrCiAsync_CiPollTimesOut_FinalizesDraftPr()
    {
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        // WaitForCompletionAsync throws inner OperationCanceledException (CI timeout fires)
        // The outer CancellationToken is NOT cancelled — simulates the ExternalCiTimeout
        var innerCts = new CancellationTokenSource();
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(innerCts.Token)); // inner timeout, outer CT not cancelled

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // Run should end as draft (CI failure path)
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()),
            Times.Once, "timed-out CI must finalize as draft");
    }

    /// <summary>
    /// WaitForPostPrCiAsync: when WaitForCompletionAsync throws a non-cancellation exception,
    /// ciGate must be set to Failed and the run finalized as draft (non-fatal error path).
    /// </summary>
    [Fact]
    public async Task WaitForPostPrCiAsync_CiPollThrowsException_FinalizesDraftPr()
    {
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("CI provider unavailable"));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()),
            Times.Once, "CI provider error must finalize as draft");
    }

    /// <summary>
    /// RunPostRetryCleanupAndFinalizeAsync: when the cleanup agent throws a non-cancellation
    /// exception, the pipeline must continue to the final quality gates (exception is swallowed,
    /// run is not aborted).
    /// </summary>
    [Fact]
    public async Task CleanupAgent_Succeeds_UpdatesFileStatsAndFinalizes()
    {
        // Covers line 194: cleanupResult != null → UpdateFileChangeStatsAsync is called.
        // Also covers lines 541-543: Coverage + SecurityScan EmitGateEvaluation via SetupValidatorAlwaysPasses.
        SetupValidatorAlwaysPasses();

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [new() { Name = "build", State = PipelineRunState.Passed }] });

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanupAgent_ThrowsException_PipelineContinuesToFinalQualityGates()
    {
        SetupValidatorAlwaysPasses();

        // Cleanup agent (second agent call) throws
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 2) // cleanup agent is the second call
                    throw new InvalidOperationException("Cleanup agent error");
                return new AgentResult
                {
                    ExitCode = 0,
                    OutputLines = ["done"],
                    Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
                };
            });

        // CommitAllAsync: first call ok (initial QG), second call ok (cleanup with changes)
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);

        // Post-PR CI passes
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // Pipeline must still call FinalizePullRequest (not abort) despite cleanup error
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once, "pipeline must finalize even if cleanup agent throws");
    }

    /// <summary>
    /// RunPostRetryCleanupAndFinalizeAsync line 194: when the cleanup agent returns a non-null
    /// result, UpdateFileChangeStatsAsync must be called and update the run's file-change stats.
    /// Covers line 194 (the <c>if (cleanupResult != null)</c> → UpdateFileChangeStatsAsync branch).
    /// </summary>
    [Fact]
    public async Task CleanupAgentSucceeds_UpdatesFileChangeStats_OnNonNullCleanupResult()
    {
        // Validator always passes so the cleanup path is reached
        SetupValidatorAlwaysPasses();

        // Provide real file-change data so UpdateFileChangeStatsAsync sets FilesChangedCount
        _mockRepoProvider
            .Setup(r => r.GetFileChangesAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChangeSummary>
            {
                new("modified", "src/Foo.cs", LinesAdded: 10, LinesDeleted: 2)
            } as IReadOnlyList<FileChangeSummary>);

        // Post-PR CI passes so the run finishes normally
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [new() { Name = "build", State = PipelineRunState.Passed }] });

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // UpdateFileChangeStatsAsync ran → FilesChangedCount updated from mock data
        _run.FilesChangedCount.Should().Be(1,
            "UpdateFileChangeStatsAsync must run when cleanup agent returns a non-null result (line 194)");
        _run.LinesAdded.Should().Be(10);
    }

    /// <summary>
    /// RunPostRetryCleanupAndFinalizeAsync lines 149-157: when post-PR CI fails after the PR has
    /// been promoted to ready-for-review, and MaxRetries > 0, the pipeline routes the failure
    /// through RunRetryLoopAsync (line 151). If retries cannot fix it, FinalizeDraftPrAsync is
    /// called at line 153.
    ///
    /// Setup: skipCiIfNoChanges path (cleanup commit throws "No changes to commit") so the pre-PR
    /// AppendExternalCiIfNeededAsync skips CI. That makes the initial retry loop pass, entering
    /// RunPostRetryCleanupAndFinalizeAsync. The cleanup QG pass also has no CI (second CommitAllAsync
    /// also throws "No changes"). FinalizePullRequest fires, then WaitForPostPrCiAsync returns CI
    /// failure → lines 149-157 execute. MaxRetries=1 so one retry fires but CI still fails → draft.
    /// </summary>
    [Fact]
    public async Task WhenPostPrCiFails_WithRetries_ExhaustsLoopThenFinalizesDraftPr()
    {
        // Validator always passes (local gates never fail)
        SetupValidatorAlwaysPasses();

        // All CommitAllAsync calls throw "No changes to commit" → skipCiIfNoChanges path skips
        // pre-PR and cleanup-path CI; still lets AppendExternalCiIfNeededAsync return early so
        // WaitForPostPrCiAsync is the ONLY place CI is evaluated.
        _mockRepoProvider
            .Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(new InvalidOperationException("No changes to commit"));

        // Post-PR CI (WaitForPostPrCiAsync + retry CI re-check): always returns failure
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [] });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Failed, Jobs = [new() { Name = "build", State = PipelineRunState.Failed, FailureReason = "CI failure" }] });

        // MaxRetries=1: one retry fires inside RunRetryLoopAsync at line 151, but CI still fails
        var context = BuildContext(maxRetries: 1);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // After retry loop exhausts with still-failing post-PR CI, must finalize as draft (line 153)
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "post-PR CI retry exhaustion must finalize run as draft PR (lines 149-157)");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-edge-abc");
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["done"],
                Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
            });
    }

    private void SetupValidatorAlwaysPasses()
    {
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "ok" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" },
                // Include Coverage and SecurityScan so EmitGateEvaluation coverage lines are hit
                Coverage = new GateResult { GateName = "Coverage", Passed = true, Details = "85%" },
                SecurityScan = new GateResult { GateName = "SecurityScan", Passed = true, Details = "ok" }
            });
    }

    private void SetupNoChangesToCommit()
    {
        _mockRepoProvider.SetupSequence(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>)
            .ThrowsAsync(new InvalidOperationException("No changes to commit"));
    }

    private QualityGateContext BuildContext(int maxRetries = 0) => new()
    {
        Run = _run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = maxRetries,
            MaxInfrastructureRetries = 0,
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(50),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        },
        AgentProvider = _mockAgent.Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = _mockPipelineProvider.Object,
        QualityGateConfigs = new[]
        {
            new QualityGateConfiguration
            {
                DisplayName = "Test QGC",
                CompilationCommand = "dotnet",
                CompilationArguments = new[] { "build" },
                TestCommand = "dotnet",
                TestArguments = new[] { "test" }
            }
        },
        Issue = new IssueDetail
        {
            Identifier = "edge#1",
            Title = "Edge case",
            Description = "Test description",
            Labels = new[] { "bug" }
        }
    };
}
