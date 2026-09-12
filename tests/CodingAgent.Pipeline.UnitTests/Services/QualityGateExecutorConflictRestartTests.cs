using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Unit tests for the conflict-restart path introduced in issue #2359.
/// Verifies that <c>PollCiWithNotStartedRetryAsync</c> detects a conflicted PR branch
/// via <c>IsPullRequestBehindBaseAsync</c> and returns <c>ConflictRestart</c> status
/// without pushing any empty commits.
/// </summary>
public class QualityGateExecutorConflictRestartPollTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IPipelineProvider> _mockPipelineProvider = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorConflictRestartPollTests()
    {
        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        // Default mocks for push/commit path
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>())).Returns(Task.CompletedTask);

        // Default: CI never starts (Pending, no jobs) — so the not-started path is taken
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });
    }

    // ── Test: Conflicted on attempt 0 → ConflictRestart, no empty commit pushed ─────────────────

    /// <summary>
    /// When CI never starts and the PR is detected as <c>Conflicted</c> on the first attempt (attempt 0),
    /// <c>PollCiWithNotStartedRetryAsync</c> must return <c>ConflictRestart</c> status without pushing any
    /// empty commit.
    /// </summary>
    [Fact]
    public async Task CiNotStarted_ConflictedOnAttempt0_ReturnsConflictRestart_NoEmptyCommitPushed()
    {
        var run = CreateRunWithPr("42");
        SetupMergeability(42, PrMergeabilityStatus.Conflicted);

        var context = BuildContext(run, ciNotStartedMaxRetries: 3);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // The gate result must indicate conflict restart
        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();
        result.ExternalCi.Details.Should().Contain("Conflict restart", "details must identify this as conflict restart");

        // run properties must be set
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart);
        run.FinalLabel.Should().Be(AgentLabels.Next);
        run.FailureReason.Should().Contain("conflict");
        run.RetryCount.Should().Be(0, "ConflictRestart must NOT increment RetryCount");

        // No empty commit must have been pushed — ConflictRestart returns before the re-push
        VerifyNoEmptyCommitPushed();
    }

    // ── Test: Conflicted on attempt N → exactly N empty commits pushed before detection ─────────

    /// <summary>
    /// When CI never starts and the PR is detected as <c>Conflicted</c> on attempt 2 (not attempt 0),
    /// exactly 2 empty commits must have been pushed (attempts 0 and 1) before conflict detection fires.
    /// No additional commit must be pushed after detection.
    /// </summary>
    [Fact]
    public async Task CiNotStarted_ConflictedOnAttemptN_ExactlyNEmptyCommitsPushedBeforeDetection()
    {
        const int conflictOnAttempt = 2;
        var run = CreateRunWithPr("99");

        // IsPullRequestBehindBaseAsync returns UpToDate for the first `conflictOnAttempt` calls,
        // then Conflicted on the (conflictOnAttempt+1)th call.
        var callCount = 0;
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount <= conflictOnAttempt
                    ? PrMergeabilityStatus.UpToDate
                    : PrMergeabilityStatus.Conflicted;
            });

        var context = BuildContext(run, ciNotStartedMaxRetries: 5);
        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Exactly conflictOnAttempt empty commits should have been pushed
        _mockRepoProvider.Verify(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(),
                It.Is<string>(s => s.Contains("not started")),
                It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()),
            Times.Exactly(conflictOnAttempt),
            $"exactly {conflictOnAttempt} empty commits expected before conflict detection");

        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart);
        run.FinalLabel.Should().Be(AgentLabels.Next);
    }

    // ── Test: Unknown mergeability → ConflictRestart NOT triggered; normal empty commit pushed ──

    /// <summary>
    /// When <c>IsPullRequestBehindBaseAsync</c> returns <c>Unknown</c>, the conflict check must
    /// not trigger <c>ConflictRestart</c> — the normal empty-commit re-trigger path must proceed.
    /// </summary>
    /// <remarks>
    /// The test keeps CI in <c>Pending</c> (no jobs) for the first poll so the not-started path
    /// is taken and <c>CheckForMergeConflictAsync</c> is actually invoked with <c>Unknown</c>.
    /// After the empty-commit re-push, CI is configured to return <c>Running</c> with jobs so
    /// the loop exits via <c>WaitForCompletionAsync</c>, confirming the normal re-trigger path
    /// completed rather than the conflict-restart path.
    /// </remarks>
    [Fact]
    public async Task CiNotStarted_UnknownMergeability_ConflictRestartNotTriggered_NormalEmptyCommitPushed()
    {
        var run = CreateRunWithPr("77");
        SetupMergeability(77, PrMergeabilityStatus.Unknown);

        // First call returns Pending (no jobs) → not-started path is entered and
        // CheckForMergeConflictAsync is called (returns Unknown → falls through).
        // Subsequent calls return Running with jobs → WaitForCompletionAsync is entered.
        var pollCallCount = 0;
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCallCount++;
                // First call (WaitForCiRunsToAppearAsync initial poll): Pending — CI not started.
                // Subsequent calls (after empty-commit re-push): Running with a job.
                return pollCallCount == 1
                    ? new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] }
                    : new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] };
            });
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] });

        var context = BuildContext(run, ciNotStartedMaxRetries: 2);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // CheckForMergeConflictAsync must have been called (CI was Pending on attempt 0)
        _mockRepoProvider.Verify(r => r.IsPullRequestBehindBaseAsync(77, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "mergeability check must be called when CI never starts and PR number is set");

        // Must NOT be ConflictRestart — Unknown must fall through to normal re-trigger
        run.CurrentStep.Should().NotBe(PipelineStep.ConflictRestart,
            "Unknown mergeability must NOT trigger ConflictRestart");
        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("normal CI path proceeds and CI passes");
    }

    // ── Test: PullRequestNumber == null → IsPullRequestBehindBaseAsync never called ─────────────

    /// <summary>
    /// When <c>run.PullRequestNumber</c> is null, <c>IsPullRequestBehindBaseAsync</c> must never be
    /// called — the check is skipped entirely and normal re-trigger behavior proceeds.
    /// </summary>
    /// <remarks>
    /// The test keeps CI in <c>Pending</c> for the first poll so the not-started path is taken
    /// and <c>CheckForMergeConflictAsync</c> would be reached. The null-PR guard inside it must
    /// cause the check to be skipped without calling <c>IsPullRequestBehindBaseAsync</c>.
    /// </remarks>
    [Fact]
    public async Task CiNotStarted_NullPullRequestNumber_MergeabilityCheckSkipped_NormalRetrigger()
    {
        var run = CreateRunWithPr(null); // no PR yet

        // Same pattern as UnknownMergeability test: first poll Pending, then Running with jobs.
        var pollCallCount = 0;
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCallCount++;
                return pollCallCount == 1
                    ? new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] }
                    : new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] };
            });
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] });

        var context = BuildContext(run, ciNotStartedMaxRetries: 2);
        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // IsPullRequestBehindBaseAsync must NEVER be called — null-PR guard in CheckForMergeConflictAsync
        _mockRepoProvider.Verify(r => r.IsPullRequestBehindBaseAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "mergeability check must be skipped entirely when PullRequestNumber is null");

        run.CurrentStep.Should().NotBe(PipelineStep.ConflictRestart);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void SetupMergeability(int prNum, PrMergeabilityStatus status)
    {
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);
    }

    private void VerifyNoEmptyCommitPushed()
    {
        _mockRepoProvider.Verify(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(),
                It.Is<string>(s => s.Contains("not started")),
                It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()),
            Times.Never,
            "no empty commit must be pushed before conflict detection on attempt 0");
    }

    private static PipelineRun CreateRunWithPr(string? prNumber) => new()
    {
        RunId = "conflict-restart-test",
        IssueIdentifier = "2359",
        IssueTitle = "Conflict restart test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"conflict-test-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2359-conflict",
        PullRequestNumber = prNumber
    };

    private QualityGateContext BuildContext(PipelineRun run, int ciNotStartedMaxRetries = 2) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = 0,
            CiCancelledMoveMaxRetries = 0,
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(1),
            CiNotStartedMaxRetries = ciNotStartedMaxRetries,
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(5),
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        },
        AgentProvider = new Mock<IAgentProvider>().Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = _mockPipelineProvider.Object,
        QualityGateConfigs = new List<QualityGateConfiguration>()
    };
}

/// <summary>
/// Tests for how <c>AppendExternalCiIfNeededAsync</c> handles the
/// <see cref="PipelineRunState.ConflictRestart"/> status returned by
/// <c>PollCiWithNotStartedRetryAsync</c>.
/// </summary>
public class QualityGateExecutorConflictRestartAppendTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IPipelineProvider> _mockPipelineProvider = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorConflictRestartAppendTests()
    {
        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>())).Returns(Task.CompletedTask);

        // Default: CI never starts (Pending, no jobs)
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });
    }

    /// <summary>
    /// When <c>PollCiWithNotStartedRetryAsync</c> returns <c>ConflictRestart</c>,
    /// <c>AppendExternalCiIfNeededAsync</c> must:
    /// <list type="bullet">
    ///   <item>Set <c>run.FinalLabel = agent:next</c></item>
    ///   <item>Leave <c>run.RetryCount</c> unchanged (0)</item>
    ///   <item>Set <c>run.CurrentStep = PipelineStep.ConflictRestart</c></item>
    ///   <item>NOT call <c>FinalizePullRequest</c> (draft PR not created)</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ConflictRestart_SetsRunProperties_DoesNotFinalizeDraftPr()
    {
        var run = CreateRunWithPr("42");
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        var context = BuildContext(run);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Assert run properties
        run.FinalLabel.Should().Be(AgentLabels.Next, "FinalLabel must be agent:next for conflict restart");
        run.RetryCount.Should().Be(0, "ConflictRestart must NOT increment RetryCount");
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart, "CurrentStep must be ConflictRestart");
        run.FailureReason.Should().NotBeNullOrEmpty("FailureReason must be set");

        // PollAndHandleInfraRetryAsync must propagate ConflictRestart before the infra-retry loop:
        // InfrastructureRetryCount must remain 0 (no infra-retry entered).
        // TODO [WARNING] (#2359): No direct test verifies that CiLogWriter.WriteJobLogs is NOT called
        // on the ConflictRestart path (CiLogWriter is a concrete dependency, not a mock here).
        // The early return in PollAndHandleInfraRetryAsync precedes the log-write, so no log is written,
        // but the write is not asserted. Adding a mockable seam for CiLogWriter would allow asserting
        // Times.Never here.
        run.InfrastructureRetryCount.Should().Be(0,
            "PollAndHandleInfraRetryAsync must propagate ConflictRestart before the infra-retry loop");

        // Assert ExternalCi gate is returned with correct details
        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();

        // FinalizePullRequest (draft PR finalization/promotion) must NOT be called — ConflictRestart
        // exits before the PR promotion step. This is the primary guard against creating a visible PR.
        _mockCallbacks.Verify(c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "FinalizeDraftPrAsync must not be called for ConflictRestart");

        // CreatePullRequest (final non-draft PR creation) must NOT be called.
        // Note: CreateDraftPrIfNotExists IS called before conflict detection (it is idempotent and a no-op
        // when the PR already exists), but CreatePullRequest would create a new visible PR, which must not
        // happen on the ConflictRestart path.
        _mockCallbacks.Verify(c => c.CreatePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "CreatePullRequest must not be called for ConflictRestart");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunWithPr(string? prNumber) => new()
    {
        RunId = "conflict-append-test",
        IssueIdentifier = "2359",
        IssueTitle = "Conflict append test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"conflict-append-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2359-append",
        PullRequestNumber = prNumber
    };

    private QualityGateContext BuildContext(PipelineRun run) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = 0,
            CiCancelledMoveMaxRetries = 0,
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(1),
            CiNotStartedMaxRetries = 2,
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(5),
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        },
        AgentProvider = new Mock<IAgentProvider>().Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = _mockPipelineProvider.Object,
        QualityGateConfigs = new List<QualityGateConfiguration>()
    };
}

/// <summary>
/// Tests verifying that <see cref="QualityGateExecutor.RunRetryLoopAsync"/> (via
/// <see cref="QualityGateExecutor.ProceedToQualityGatesAsync"/>) exits immediately when
/// <see cref="AppendExternalCiIfNeededAsync"/> sets <c>ConflictRestart</c> during a retry iteration.
/// </summary>
public class QualityGateExecutorConflictRestartRetryLoopTests
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

    private static readonly string[] AgentFixOutputLines = ["Fixed the issue"];

    // Initial QG fails (so the retry loop is entered)
    private static readonly QualityGateReport InitialFailingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "Tests failed" }
    };

    // In-loop QG passes compilation+tests so AppendExternalCiIfNeededAsync proceeds to the CI poll.
    // AppendExternalCiIfNeededAsync exits early when Compilation or Tests failed, so passing
    // compilation+tests is required to reach the conflict check inside the method.
    private static readonly QualityGateReport InLoopPassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    public QualityGateExecutorConflictRestartRetryLoopTests()
    {
        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // First QG call (pre-loop): fails — so the retry loop is entered
        // Subsequent calls (in-loop): pass compilation+tests — so AppendExternalCiIfNeededAsync
        // proceeds past the early-exit guard and reaches the CI poll / conflict check
        // TODO [WARNING]: Replace this shared captured-integer sequencing with SetupSequence.
        // The current approach has two problems:
        // 1. Cross-test contamination: xUnit creates one instance per test class for [Fact] tests,
        //    so validatorCallCount is shared across all tests in this class. The second test starts
        //    with the counter already incremented by the first, causing it to return InLoopPassingReport
        //    on the very first call — the retry loop is never entered and both Times.Once and
        //    ConflictRestart assertions may pass vacuously or fail unexpectedly depending on run order.
        // 2. Order-fragility: if a future code path adds a ValidateAsync call before the retry loop,
        //    the sequence shifts silently — the first in-loop call gets InitialFailingReport instead
        //    of InLoopPassingReport, exercising a different path with no signal.
        // Fix: _mockValidator.SetupSequence(v => v.ValidateAsync(...))
        //          .ReturnsAsync(InitialFailingReport)
        //          .ReturnsAsync(InLoopPassingReport);
        var validatorCallCount = 0;
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(() =>
            {
                validatorCallCount++;
                // First call: initial QG before the retry loop — fails so loop is entered
                // Subsequent calls: inside the retry loop — passes so CI check is reached
                return validatorCallCount == 1 ? InitialFailingReport : InLoopPassingReport;
            });

        // Agent returns a real non-null, non-empty result so RunFixAgentIterationAsync falls through
        // to quality gate validation. Without this, Moq returns null → ClassifyRetryOutcome(null)
        // → RetryOutcome.TransientWait → continue, bypassing AppendExternalCiIfNeededAsync entirely.
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = AgentFixOutputLines,
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        // History service needed for CollectFailureFeedbackAsync (reached if guard is absent)
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // CI never starts (Pending, no jobs) → not-started path is taken, conflict check fires
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // Repo default stubs (needed by AppendExternalCiIfNeededAsync commit/push path)
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");

        // Callback stubs
        _mockCallbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// When <c>AppendExternalCiIfNeededAsync</c> detects a merge conflict during the retry loop,
    /// <c>RunRetryLoopAsync</c> must exit immediately without invoking the fix agent a second time.
    /// With MaxRetries=2 and ConflictRestart on iteration 1, a buggy implementation re-enters the
    /// loop and calls the agent twice. The fix ensures the agent is called exactly once.
    /// </summary>
    [Fact]
    public async Task ConflictRestart_DuringRetryLoop_ReturnsImmediately_NoFixAgentCalledAgain()
    {
        var run = CreateRunWithPr("42");
        // Conflicted on every check → AppendExternalCiIfNeededAsync sets ConflictRestart on iteration 1
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        // MaxRetries=2 so the loop would continue to iteration 2 if the guard is absent
        var context = BuildContext(run, maxRetries: 2);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Fix agent must be called exactly once (iteration 1 only).
        // A buggy implementation calls it twice: once before ConflictRestart is set, and again
        // on iteration 2 because the loop re-enters after LogAndRecordReport runs.
        // Filter out the feedback agent call (contains "Pipeline Failure Feedback") so only
        // fix-agent calls are counted.
        // TODO [WARNING]: The negative filter (!r.Prompt.Contains("Pipeline Failure Feedback")) makes
        // Times.Once pass vacuously if CollectFailureFeedbackAsync is never reached — the filtered
        // count is 1 regardless of whether the feedback path ran. Add a complementary assertion:
        //   _mockAgent.Verify(
        //       a => a.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback")), ...),
        //       Times.AtLeastOnce,
        //       "Feedback-agent call must have occurred to validate the filter is not vacuous");
        // Without it, a regression that skips the feedback path entirely still passes this test.
        _mockAgent.Verify(
            a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => !r.Prompt.Contains("Pipeline Failure Feedback")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Once,
            "Fix agent must be called exactly once — ConflictRestart must prevent iteration 2");

        // Positive evidence: ConflictRestart was actually reached (not a vacuous pass)
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart);
        run.FinalLabel.Should().Be(AgentLabels.Next);
        // TODO [WARNING]: The acceptance criterion "RunRetryLoopAsync returns without calling
        // LogAndRecordReport" is only partially covered by the Times.Once check above. A bug that
        // calls LogAndRecordReport once before returning would still pass. LogAndRecordReport has
        // an observable side effect via run.QualityGateHistory.Enqueue — assert on its count to
        // close the gap, e.g.: run.QualityGateHistory.Count.Should().Be(1,
        //   "only the pre-loop LogAndRecordReport call should have enqueued — not the in-loop one");
    }

    /// <summary>
    /// When <c>ConflictRestart</c> is set inside the retry loop, <c>ProceedToQualityGatesAsync</c>
    /// must not call <c>FinalizePullRequest</c>. Without the call-site guard, the run falls through
    /// to <c>FinalizeDraftPrAsync</c>, which incorrectly promotes the draft PR on a run already
    /// re-queued via <c>agent:next</c>.
    /// </summary>
    [Fact]
    public async Task ConflictRestart_DuringRetryLoop_DoesNotCallFinalizePullRequest()
    {
        var run = CreateRunWithPr("42");
        _mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        var context = BuildContext(run, maxRetries: 2);
        await _executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Positive evidence: ConflictRestart was actually reached (not a vacuous pass)
        run.CurrentStep.Should().Be(PipelineStep.ConflictRestart,
            "ConflictRestart must have been set — otherwise this test proves nothing");

        // FinalizePullRequest must not be called — the run is already re-queued via agent:next
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "FinalizePullRequest must not be called when ConflictRestart exits the retry loop");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunWithPr(string? prNumber) => new()
    {
        RunId = "conflict-retry-loop-test",
        IssueIdentifier = "2464",
        IssueTitle = "ConflictRestart retry loop test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"conflict-retry-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2464-conflict",
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
            Identifier = "2464",
            Title = "ConflictRestart retry loop test",
            Description = "Test issue description",
            Labels = ["bug"]
        }
    };
}
