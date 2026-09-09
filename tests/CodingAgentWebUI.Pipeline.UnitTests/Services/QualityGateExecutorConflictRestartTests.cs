using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

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
