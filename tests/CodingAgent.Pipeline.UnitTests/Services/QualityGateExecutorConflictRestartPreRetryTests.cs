using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Regression tests for issue #3045: the pre-retry-loop guard in
/// <see cref="QualityGateExecutor.ProceedToQualityGatesAsync"/> was missing
/// <see cref="PipelineStep.ConflictRestart"/>, causing the fix agent to be invoked and
/// <c>run.RetryCount</c> to be incremented even when the first quality gate pass detected
/// a conflicted PR branch.
///
/// These tests cover the <em>pre-retry-loop</em> path — where <c>AppendExternalCiIfNeededAsync</c>
/// is called before the retry loop is entered. Contrast with
/// <c>QualityGateExecutorConflictRestartRetryLoopTests</c> which covers the in-loop path (fixed in #2464).
/// </summary>
public class QualityGateExecutorConflictRestartPreRetryTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator = new();
    private readonly Mock<IAgentProvider> _mockAgent = new();
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IPipelineProvider> _mockPipelineProvider = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly QualityGateExecutor _executor;

    // The initial QG passes so AppendExternalCiIfNeededAsync is entered.
    // AppendExternalCiIfNeededAsync exits early when Compilation or Tests failed,
    // so a passing report is required to reach the CI poll / conflict check.
    private static readonly QualityGateReport PassingQgReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    public QualityGateExecutorConflictRestartPreRetryTests()
    {
        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // QG passes on every call — this is the pre-retry-loop path so only one call is expected.
        // Using SetupSequence avoids the cross-test contamination risk present in the sibling
        // QualityGateExecutorConflictRestartRetryLoopTests class (see TODO there).
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(PassingQgReport);

        // CI never starts (Pending, no jobs) → not-started path is taken, conflict check fires.
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // Commit/push stubs needed by AppendExternalCiIfNeededAsync's CommitAndPushAsync.
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");

        // Callback stubs — must all complete so no secondary exception obscures test assertions.
        _mockCallbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _mockCallbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _mockCallbacks.Setup(c => c.NotifyChange());
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // History service needed for CollectFailureFeedbackAsync (reached if fix is absent and
        // loop exhausts MaxRetries, falling through to FinalizeDraftPrAsync).
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // Agent mock: if ConflictRestart guard is absent the fix agent will be invoked.
        // Return a real result so HandleDefaultRetryAsync increments RetryCount — this makes
        // the regression immediately visible as RetryCount == 1 instead of 0.
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Fixed the issue"],
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
    }

    // ── Test 1 (Repro): RetryCount must remain 0 ──────────────────────────────────────────────
    // TODO: [WARNING] The section headings reference "Test 1" and "Test 3" but there is no "Test 2".
    // The numbering gap suggests a test was either accidentally removed during development or the
    // numbering was not cleaned up after a plan change. Either add the missing test (e.g., MaxRetries=0
    // boundary or ConflictRestart with a null PR number) or remove the numeric labels and use
    // descriptive headings only.

    /// <summary>
    /// Reproduction test for issue #3045.
    ///
    /// When <c>AppendExternalCiIfNeededAsync</c> detects a conflicted PR on the <em>first</em> quality
    /// gate pass (before the retry loop), <c>ProceedToQualityGatesAsync</c> must return immediately
    /// without entering <c>RunRetryLoopAsync</c>. Before the fix, the missing <c>ConflictRestart</c>
    /// guard at line 44 caused the retry loop to be entered, invoking the fix agent and consuming
    /// one retry budget slot.
    ///
    /// Passing condition: <c>run.RetryCount == 0</c> and the fix agent is never invoked.
    /// </summary>
    [Fact]
    public async Task Repro_ProceedToQualityGates_WhenConflictRestartOnFirstGate_DoesNotConsumeRetry()
    {
        var run = CreateRunWithPr("42");
        // Conflicted on every check → AppendExternalCiIfNeededAsync sets ConflictRestart.
        // TODO: [WARNING] The constructor configures _mockPipelineProvider to return PipelineRunState.Pending
        // with no jobs, activating a CI-not-started code path inside AppendExternalCiIfNeededAsync. The
        // ConflictRestart is triggered downstream by IsPullRequestBehindBaseAsync returning Conflicted —
        // but only if the CI-not-started path does not early-return first (e.g., CiNotStartedMaxRetries
        // exhaustion). With CiNotStartedTimeout=1ms and CiNotStartedMaxRetries=2 in BuildContext, the test
        // depends on the CI-not-started retry logic exhausting its retries and then calling IsPullRequestBehindBaseAsync
        // before returning. The run.CurrentStep.Should().Be(ConflictRestart) assertion provides positive evidence
        // that the correct path was taken, mitigating a vacuous-pass risk — but the test is more fragile than
        // necessary. A more robust approach would be to configure the mock so that AppendExternalCiIfNeededAsync
        // reaches the conflict check directly without relying on CI timing constants.
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        // MaxRetries=2: without the fix the loop executes and RetryCount reaches 1.
        var context = BuildContext(run, maxRetries: 2);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // PRIMARY ACCEPTANCE CRITERION: retry budget must be preserved.
        run.RetryCount.Should().Be(0,
            "ConflictRestart on the first gate must not consume a retry slot — " +
            "the pre-retry guard must prevent RunRetryLoopAsync from being entered");

        // Positive evidence: ConflictRestart was actually reached (not a vacuous pass).
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart,
            "ConflictRestart must have been set by BuildConflictRestartReport");
        run.FinalLabel.Should().Be(AgentLabels.Next,
            "FinalLabel must be agent:next so the run is re-queued for rework");

        // Fix agent must never be invoked — there is nothing for it to fix on a conflicted branch.
        _mockAgent.Verify(
            a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Never,
            "Fix agent must not be invoked — ConflictRestart must prevent RunRetryLoopAsync from being entered");

        // FinalizeRunAsync must not be re-entered — the run is already finalized as ConflictRestart.
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "SwapLabelAsync must not be called — FinalizeRunAsync must not be entered after ConflictRestart");
    }

    // ── Test 3 (No-finalize guard): FinalizePullRequest must not be called ───────────────────
    // TODO: [WARNING] This test is structurally a duplicate of Repro_ProceedToQualityGates_WhenConflictRestartOnFirstGate_DoesNotConsumeRetry:
    // identical setup, same IsPullRequestBehindBaseAsync stub, same CurrentStep and RetryCount assertions.
    // The only net-new coverage is the two Times.Never verifications on FinalizePullRequest and CreatePullRequest.
    // Consider merging those verifications into the reproduction test instead, or removing the duplicated
    // RetryCount==0 assertion here (it is already covered by Test 1 and its failure here would always coincide
    // with a Test 1 failure, obscuring which behavior regressed).

    /// <summary>
    /// When <c>AppendExternalCiIfNeededAsync</c> sets <c>ConflictRestart</c> on the first gate pass,
    /// <c>ProceedToQualityGatesAsync</c> must not call <c>FinalizePullRequest</c> or
    /// <c>CreatePullRequest</c>. Without the guard, execution falls through to
    /// <c>RunPostRetryCleanupAndFinalizeAsync</c> or <c>FinalizeDraftPrAsync</c>, which would
    /// promote the draft PR on a run that has already been re-queued via <c>agent:next</c>.
    /// </summary>
    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenConflictRestartOnFirstGate_DoesNotCallFinalizePullRequest()
    {
        var run = CreateRunWithPr("42");
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        var context = BuildContext(run, maxRetries: 2);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Positive evidence: ConflictRestart was actually reached (not a vacuous pass).
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart,
            "ConflictRestart must have been set — otherwise this test proves nothing");

        // PRIMARY ACCEPTANCE CRITERION: retry budget must be preserved.
        run.RetryCount.Should().Be(0,
            "ConflictRestart on the first gate must not consume a retry slot");

        // FinalizePullRequest must not be called — the run is already re-queued via agent:next.
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "FinalizePullRequest must not be called when ConflictRestart exits the pre-retry guard");

        // CreatePullRequest must not be called either.
        _mockCallbacks.Verify(
            c => c.CreatePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "CreatePullRequest must not be called when ConflictRestart exits the pre-retry guard");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunWithPr(string? prNumber) => new()
    {
        RunId = "conflict-pre-retry-test",
        IssueIdentifier = "3045",
        IssueTitle = "ConflictRestart pre-retry guard test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"conflict-pre-retry-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-3045-conflict",
        PullRequestNumber = prNumber
    };

    private QualityGateContext BuildContext(PipelineRun run, int maxRetries = 2) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = maxRetries,
            MaxInfrastructureRetries = 0,
            CiCancelledMoveMaxRetries = 0,
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(1),
            CiNotStartedMaxRetries = 2,
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(5),
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1),
            TransientRetryDelay = TimeSpan.Zero
        },
        AgentProvider = _mockAgent.Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = _mockPipelineProvider.Object,
        QualityGateConfigs = new List<QualityGateConfiguration>
        {
            new()
            {
                DisplayName = "Test QGC",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                TestCommand = "dotnet",
                TestArguments = ["test"]
            }
        },
        Issue = new IssueDetail
        {
            Identifier = "3045",
            Title = "ConflictRestart pre-retry guard test",
            Description = "Test issue description",
            Labels = ["bug"]
        }
    };
}
