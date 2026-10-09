using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Characterization tests for the <c>RunPostRetryCleanupAndFinalizeAsync</c> post-cleanup
/// ConflictRestart exit path — the bug fixed in issue #3234.
///
/// Before the fix, the guard after <c>AppendExternalCiIfNeededAsync</c> inside
/// <c>RunPostRetryCleanupAndFinalizeAsync</c> omitted <c>ConflictRestart</c>. If
/// <c>AppendExternalCiIfNeededAsync</c> detected a conflicted PR on the final quality gate pass
/// (after the cleanup agent ran), execution fell through to <c>RunRetryLoopAsync</c>, invoking the
/// fix agent and consuming a retry slot on a branch GitHub cannot build.
///
/// After the fix, the guard uses <c>IsQualityGateExitState()</c> which includes
/// <c>ConflictRestart</c>, so execution returns immediately.
/// </summary>
public class QualityGateExecutorConflictRestartPostCleanupTests
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

    // First QG pass (before retry loop) passes — so RunPostRetryCleanupAndFinalizeAsync is entered.
    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    public QualityGateExecutorConflictRestartPostCleanupTests()
    {
        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // Both QG calls return the passing report:
        //   call 1 — the initial quality gate pass (before the retry loop); passes → enters RunPostRetryCleanupAndFinalizeAsync
        //   call 2 — the final quality gate pass inside RunPostRetryCleanupAndFinalizeAsync; passes →
        //            AppendExternalCiIfNeededAsync is entered (it exits early on a failing report)
        // On the second call, AppendExternalCiIfNeededAsync triggers ConflictRestart (via the CI-not-started
        // conflict-check path). Both calls returning PassingReport is correct because ConflictRestart is
        // set by AppendExternalCiIfNeededAsync, not by the validator.
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PassingReport);

        // CI never starts (Pending, no jobs) → not-started path is taken, conflict check fires
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // Commit/push stubs needed by AppendExternalCiIfNeededAsync
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");

        // Callback stubs
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

        // Agent mock: if the ConflictRestart guard is absent the cleanup retry loop will invoke the fix agent.
        // Return a real result so HandleDefaultRetryAsync increments RetryCount, making the regression
        // immediately visible as RetryCount == 1 instead of 0.
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

        // History service needed for CollectFailureFeedbackAsync (reached if fix is absent and
        // the cleanup retry loop exhausts MaxRetries, falling through to FinalizeDraftPrAsync).
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
    }

    /// <summary>
    /// Reproduction test for issue #3234 (post-cleanup path bug).
    ///
    /// When <c>AppendExternalCiIfNeededAsync</c> detects a conflicted PR on the final quality gate
    /// pass inside <c>RunPostRetryCleanupAndFinalizeAsync</c>, <c>ProceedToQualityGatesAsync</c>
    /// must return immediately without entering <c>RunRetryLoopAsync</c> and without consuming
    /// a retry slot.
    ///
    /// Before the fix: the guard omitted <c>ConflictRestart</c>, causing the retry loop to be
    /// entered and <c>run.RetryCount</c> to be incremented.
    /// After the fix: the guard uses <c>IsQualityGateExitState()</c> and returns immediately.
    /// </summary>
    [Fact]
    public async Task RunPostRetryCleanupAndFinalizeAsync_WhenConflictRestartOnFinalGate_DoesNotConsumeRetry()
    {
        var run = CreateRunWithPr("42");
        // Conflicted on every check → AppendExternalCiIfNeededAsync sets ConflictRestart on the
        // final quality gate pass inside RunPostRetryCleanupAndFinalizeAsync.
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        // MaxRetries=2: without the fix the cleanup retry loop executes and RetryCount reaches 1.
        var context = BuildContext(run, maxRetries: 2);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // PRIMARY ACCEPTANCE CRITERION: retry budget must be preserved.
        // TODO [WARNING]: This assertion verifies RetryCount == 0 after the full ProceedToQualityGatesAsync
        // call, but the call also traverses the pre-cleanup retry path (the first quality-gate pass
        // before entering RunPostRetryCleanupAndFinalizeAsync). If that path incremented RetryCount due
        // to an unrelated bug, the assertion fails for a reason unrelated to the ConflictRestart guard
        // being tested here. To pin the assertion precisely to the post-cleanup path, capture RetryCount
        // just before RunPostRetryCleanupAndFinalizeAsync is entered (e.g. via a callback hook or by
        // asserting the *delta* is zero), or restructure the test to call RunPostRetryCleanupAndFinalizeAsync
        // directly when/if it becomes accessible without the pre-cleanup traversal.
        run.RetryCount.Should().Be(0,
            "ConflictRestart on the final cleanup gate must not consume a retry slot — " +
            "IsQualityGateExitState() must prevent RunRetryLoopAsync from being entered");

        // Positive evidence: ConflictRestart was actually reached (not a vacuous pass).
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart,
            "ConflictRestart must have been set by AppendExternalCiIfNeededAsync on the final cleanup pass");
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
    }

    /// <summary>
    /// When <c>AppendExternalCiIfNeededAsync</c> sets <c>ConflictRestart</c> on the final cleanup
    /// gate pass, <c>ProceedToQualityGatesAsync</c> must not call <c>FinalizePullRequest</c>.
    /// Without the guard, execution would fall through to <c>RunRetryLoopAsync</c> and eventually
    /// <c>FinalizeDraftPrAsync</c>, promoting the draft PR on a run already re-queued via
    /// <c>agent:next</c>.
    /// </summary>
    [Fact]
    public async Task RunPostRetryCleanupAndFinalizeAsync_WhenConflictRestartOnFinalGate_DoesNotCallFinalizePullRequest()
    {
        var run = CreateRunWithPr("42");
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        var context = BuildContext(run, maxRetries: 2);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Positive evidence: ConflictRestart was actually reached (not a vacuous pass).
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart,
            "ConflictRestart must have been set — otherwise this test proves nothing");

        // RetryCount must be preserved to confirm the guard fired before RunRetryLoopAsync.
        run.RetryCount.Should().Be(0,
            "ConflictRestart must not consume a retry slot");

        // FinalizePullRequest must not be called — the run is already re-queued via agent:next.
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "FinalizePullRequest must not be called when ConflictRestart exits the post-cleanup guard");

        // CreatePullRequest must not be called either.
        _mockCallbacks.Verify(
            c => c.CreatePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "CreatePullRequest must not be called when ConflictRestart exits the post-cleanup guard");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunWithPr(string? prNumber) => new()
    {
        RunId = "conflict-post-cleanup-test",
        IssueIdentifier = "3234",
        IssueTitle = "ConflictRestart post-cleanup guard test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"conflict-post-cleanup-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-3234-conflict",
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
            Identifier = "3234",
            Title = "ConflictRestart post-cleanup guard test",
            Description = "Test issue description",
            Labels = ["bug"]
        }
    };
}
