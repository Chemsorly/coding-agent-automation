using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Verifies that external CI polling always filters by commit SHA,
/// including when a PR exists (regression test for #542).
/// </summary>
public class QualityGateExecutorCiPollingTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorCiPollingTests()
    {
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        SetupDefaultMocks();
    }

    [Fact]
    public async Task AppendExternalCi_WithPullRequestNumber_PassesShaToPoller()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";

        var context = BuildContext(run);

        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, "sha-head-abc", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AppendExternalCi_WithPullRequestNumber_InfraRetry_PassesShaToPoller()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";

        // First call: infrastructure failure; second call: passes
        var infraFailure = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new()
                {
                    Name = "build", State = PipelineRunState.Failed,
                    LogContent = "lost communication with the server"
                }
            }
        };
        var passed = new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() };

        _mockPipelineProvider.SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(infraFailure)
            .ReturnsAsync(passed);

        // Infra retry creates an empty commit + push, then reads new SHA
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);

        var context = BuildContext(run);

        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Both initial poll and retry poll should pass the SHA (not null)
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, "sha-head-abc", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// Acceptance criterion (issue #2798): after the fix, the combined poll-plus-infra-retry session
    /// is bounded by a single ExternalCiTimeout window.
    ///
    /// Scenario: initial poll → Cancelled → branch moved → re-poll → infrastructure failure →
    /// infra-retry starts and blocks in WaitForCompletionAsync → ExternalCiTimeout fires →
    /// AppendExternalCiIfNeededAsync returns a "timed out" gate result WITHOUT waiting for a second
    /// full ExternalCiTimeout.
    ///
    /// Before the fix, ExecuteInfraRetryAsync was passed the raw outer `ct` (never cancelled), so
    /// its WaitForCompletionAsync would block indefinitely past ExternalCiTimeout.
    /// After the fix, it receives `pollCt` (linked to timeoutCts), so the timeout fires and the
    /// OCE propagates through ExecuteInfraRetryAsync → PollAndHandleInfraRetryAsync →
    /// AppendExternalCiIfNeededAsync's catch (OperationCanceledException) when (!ct.IsCancellationRequested),
    /// which converts it to a failing "timed out" gate result.
    /// </summary>
    [Fact]
    public async Task AppendExternalCi_WhenInfraRetryRunsAfterBranchMovedPolls_IsBoundedBySingleExternalCiTimeout()
    {
        var run = CreateRun();

        // ── SHA sequence ─────────────────────────────────────────────────────
        // Call 1: after CommitAndPushAsync (initial push) → "sha-original"
        // Call 2: inside branch-moved loop after Cancelled → "sha-moved" (different, triggers re-poll)
        // Call 3+: any subsequent reads (infra-retry's own SHA read after its push)
        // TODO [WARNING] (#2798 DotNetSpecialist): SetupSequence registers exactly 3 return values.
        // If ExecuteInfraRetryAsync calls TryReadHeadShaAsync more than once (e.g. future retry
        // logic), the sequence exhausts and Moq returns null by default — silently changing test
        // behaviour without a failure. Add a catch-all Setup after the sequence to return
        // "sha-infra-retry" for any call beyond the third, making the fallback explicit.
        _mockRepoProvider.SetupSequence(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-original")
            .ReturnsAsync("sha-moved")
            .ReturnsAsync("sha-infra-retry");

        // ── GetRunStatusAsync — CRITICAL: must return non-Pending with jobs ──
        // WaitForCiRunsToAppearAsync loops on GetRunStatusAsync until non-Pending.
        // If this returns Pending, WaitForCompletionAsync is never reached and the test is vacuous.
        // Keep the class-level default (Running with one job) — it already satisfies this requirement.
        // TODO [WARNING] (#2798 TestQualityReviewer): This test implicitly relies on the class-level
        // SetupDefaultMocks() returning Running for GetRunStatusAsync. If that default is changed
        // (e.g. to Pending), WaitForCiRunsToAppearAsync will time out after CiNotStartedTimeout
        // (50 ms) and the infra-retry path is never reached — causing a misleading Times.AtLeast(2)
        // failure. Consider explicitly setting up GetRunStatusAsync in this test to remove the
        // implicit dependency and make the setup self-documenting.

        // ── Infrastructure-classified failure — CRITICAL ──────────────────────
        // CiFailureClassifier.Classify only returns Infrastructure when LogContent matches known
        // infra-error strings. A generic Failed status returns Unknown and the while loop is a no-op.
        var infraFailureStatus = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Failed, LogContent = "lost communication with the server" }
            }
        };

        // ── TaskCompletionSource gate for the infra-retry WaitForCompletionAsync call ──
        // The TCS never completes — WaitForCompletionAsync blocks until pollCt fires.
        var infraRetryBlocker = new TaskCompletionSource<PipelineRunStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        // WaitForCompletionAsync dispatch by call count:
        //   Call 1 (initial poll)      → Cancelled (triggers branch-moved loop)
        //   Call 2 (branch-moved poll) → infrastructure failure (triggers infra-retry loop)
        //   Call 3 (infra-retry poll)  → blocks via TCS.WaitAsync(token) until pollCt fires
        var waitCallCount = 0;
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string? _, TimeSpan _, CancellationToken token) =>
            {
                var callIndex = Interlocked.Increment(ref waitCallCount);
                return callIndex switch
                {
                    1 => new PipelineRunStatus { State = PipelineRunState.Cancelled, Jobs = new List<PipelineJobResult>() },
                    2 => infraFailureStatus,
                    _ => await infraRetryBlocker.Task.WaitAsync(token)
                };
            });

        // ── CommitAllAsync: empty-commit overload used by ExecuteInfraRetryAsync ──
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);

        // ── Context with short ExternalCiTimeout so timeoutCts fires quickly ──
        // CiCancelledMoveMaxRetries = 1 → branch-moved loop fires once.
        // MaxInfrastructureRetries = 1 → infra-retry while loop fires at least once.
        // Outer ct = CancellationToken.None → only pollCt fires, not the pipeline CT.
        // TODO [WARNING] (#2798 Correctness/TestQualityReviewer): ExternalCiTimeout = 500 ms is
        // marginal. On a slow/loaded CI host, timeoutCts may fire before all three
        // WaitForCompletionAsync calls complete, causing a green result via an earlier-circuit OCE
        // path (e.g. from WaitForCiRunsToAppearAsync) rather than from the infra-retry blocker —
        // making the Times.AtLeast(2) verify potentially fail non-deterministically. Consider
        // increasing ExternalCiTimeout to at least 5 seconds (and reducing CiNotStartedTimeout to
        // 1 ms) to ensure the infra-retry WaitForCompletionAsync call is always reached before the
        // budget expires. Also consider strengthening the Verify to Times.Exactly(3) to pin that
        // the timeout fires specifically during the third (infra-retry) call.
        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = 1,
                CiCancelledMoveMaxRetries = 1,
                ExternalCiTimeout = TimeSpan.FromMilliseconds(500),
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(10),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                CiNotStartedMaxRetries = 0,
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

        // ── Execute ──────────────────────────────────────────────────────────
        // Outer ct = CancellationToken.None — the pipeline CT is NOT cancelled.
        // After the fix, pollCt fires after ExternalCiTimeout (500 ms) and the OCE propagates
        // to AppendExternalCiIfNeededAsync's catch (OCE) when (!ct.IsCancellationRequested) handler.
        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // ── Assert ───────────────────────────────────────────────────────────
        result.ExternalCi.Should().NotBeNull(
            "infra-retry timeout OCE must be caught by AppendExternalCiIfNeededAsync and converted to a gate result");
        result.ExternalCi!.Passed.Should().BeFalse(
            "external CI gate must fail when ExternalCiTimeout fires during infra-retry");
        result.ExternalCi.Details.Should().Contain("timed out",
            "Details must identify ExternalCiTimeout as the cause (BuildCiTimeoutGateResult path)");

        // WaitForCompletionAsync was called at least twice (initial poll + branch-moved re-poll)
        // before reaching the infra-retry, confirming the full sequence exercised ExecuteInfraRetryAsync.
        // TODO [WARNING] (#2798 TestQualityReviewer): Times.AtLeast(2) is too weak — it passes even
        // if the infra-retry loop ran multiple times or the infra-retry call was never reached (if
        // 2 calls happened before it). Consider Times.Exactly(3) with a descriptive message
        // ("initial poll, branch-moved re-poll, infra-retry block") to precisely pin that the
        // timeout fires during the third call only.
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2),
            "at minimum the initial poll and branch-moved re-poll must have run before the infra-retry block");
    }

    [Fact]
    public async Task AppendExternalCi_WhenShaReadFails_PassesNullToPoller()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";

        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("git not available"));

        var context = BuildContext(run);

        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Graceful degradation: null SHA means branch-only filtering
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO [WARNING]: The infra-retry SHA read (retrySha, QualityGateExecutor.ExternalCi.cs ~L344) and the
    // post-re-push SHA read (pollSha, QualityGateExecutor.ExternalCi.cs ~L493) have no failure-path tests.
    // Both will correctly return null via TryReadHeadShaAsync on non-cancellation exceptions, but this
    // behaviour is untested. Consider adding tests analogous to AppendExternalCi_WhenShaReadFails_PassesNullToPoller
    // for those two code paths (infra-retry branch and re-push loop branch) to lock in the graceful-degradation
    // contract introduced by the TryReadHeadShaAsync extraction (issue #2622).

    /// <summary>
    /// Acceptance criterion (issue #2674): when the per-poll CancellationTokenSource timeout fires
    /// (e.g. inside PollAndHandleInfraRetryAsync) and propagates as an OperationCanceledException,
    /// the outer catch in AppendExternalCiIfNeededAsync must intercept it (outer ct is NOT cancelled)
    /// and return a failing GateResult — the exception must not propagate to the caller.
    /// This variant exercises the WaitForCompletionAsync call site (inside the polling helper).
    /// </summary>
    [Fact]
    public async Task AppendExternalCi_WhenPerPollTimeoutOceFiredByTryReadHeadSha_ReturnsFailingGateResult()
    {
        var run = CreateRun();

        // Simulate per-poll timeout: WaitForCompletionAsync throws OCE with an independent,
        // already-cancelled CTS — NOT the outer ct. This represents the pollCt (linked timeout token)
        // being cancelled by timeoutCts firing inside PollAndHandleInfraRetryAsync.
        // TODO [WARNING]: The OCE here originates from WaitForCompletionAsync, not directly from
        // GetHeadCommitShaAsync (TryReadHeadShaAsync). The test exercises the outer catch clause
        // correctly but does not confirm the TryReadHeadShaAsync call site specifically. See the
        // companion test AppendExternalCi_WhenPerPollTimeoutOceFiredByGetHeadCommitSha_ReturnsFailingGateResult
        // which injects via GetHeadCommitShaAsync.
        using var perPollCts = new CancellationTokenSource();
        perPollCts.Cancel();
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(perPollCts.Token));

        var context = BuildContext(run);

        // Outer ct = CancellationToken.None — the pipeline CT is NOT cancelled.
        // The when (!ct.IsCancellationRequested) guard must be true → exception is caught and
        // converted to a failing GateResult; it must NOT propagate.
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull("per-poll timeout OCE must be converted to a failing gate, not propagated");
        result.ExternalCi!.Passed.Should().BeFalse("external CI gate must fail when per-poll timeout fires");
        // TODO [WARNING]: The "timed out" substring check is coupled to the production message
        // format ("External CI timed out after {config.ExternalCiTimeout}"). If the wording changes
        // this assertion fails with an opaque message. The primary contract (no exception, Passed == false)
        // is already verified above; the string check adds fragility without meaningful additional coverage.
        result.ExternalCi.Details.Should().Contain("timed out", "Details must identify the timeout as the cause");
    }

    /// <summary>
    /// Acceptance criterion (issue #2674): confirms that an OCE originating specifically from
    /// <c>TryReadHeadShaAsync</c> → <c>GetHeadCommitShaAsync</c> with a per-poll timeout token
    /// (outer pipeline CT is NOT cancelled) is caught by the
    /// <c>when (!ct.IsCancellationRequested)</c> guard and converted to a failing GateResult.
    /// This directly exercises the regression scenario described in the issue: a future refactor
    /// that makes TryReadHeadShaAsync swallow OCE again would leave this test green while the
    /// per-poll OCE is silently dropped; the test pins the observable contract from that call site.
    /// </summary>
    [Fact]
    public async Task AppendExternalCi_WhenPerPollTimeoutOceFiredByGetHeadCommitSha_ReturnsFailingGateResult()
    {
        var run = CreateRun();

        // Inject OCE from GetHeadCommitShaAsync with an independent cancelled CTS (not the outer ct).
        // TryReadHeadShaAsync rethrows OCE unconditionally; the outer catch in
        // AppendExternalCiIfNeededAsync must intercept it because ct.IsCancellationRequested == false.
        using var perPollCts = new CancellationTokenSource();
        perPollCts.Cancel();
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(perPollCts.Token));

        var context = BuildContext(run);

        // Outer ct = CancellationToken.None — the pipeline CT is NOT cancelled.
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull("OCE from TryReadHeadShaAsync must be converted to a failing gate, not propagated");
        result.ExternalCi!.Passed.Should().BeFalse("external CI gate must fail when TryReadHeadShaAsync throws per-poll OCE");
        result.ExternalCi.Details.Should().Contain("timed out", "Details must identify the timeout as the cause");
    }

    /// <summary>
    /// Acceptance criterion (issue #2674): when the pipeline CancellationToken (the outer ct) is
    /// cancelled and an OperationCanceledException propagates from inside the try block, the outer
    /// catch in AppendExternalCiIfNeededAsync must rethrow it — the run is being torn down and the
    /// exception must reach the caller.
    /// </summary>
    [Fact]
    public async Task AppendExternalCi_WhenPipelineCancelled_OcePropagates()
    {
        var run = CreateRun();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // CommitAllAsync is the first async call inside the try block. Throwing with the outer ct
        // (ct.IsCancellationRequested == true) exercises the unconditional rethrow path:
        //   catch (OperationCanceledException) { throw; }
        // NOTE: This test confirms the rethrow path fires for pre-TryReadHeadShaAsync pipeline-CT
        // cancellation. See AppendExternalCi_WhenPipelineCancelledAtTryReadHeadSha_OcePropagates
        // for the complementary test that injects OCE at the TryReadHeadShaAsync call site itself.
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var context = BuildContext(run);

        var act = async () => await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "pipeline CT cancellation must propagate out of AppendExternalCiIfNeededAsync");
    }

    /// <summary>
    /// Acceptance criterion (issue #2674): when the pipeline CancellationToken (the outer ct) is
    /// cancelled and an OperationCanceledException propagates specifically from the
    /// <c>TryReadHeadShaAsync</c> call site (<c>GetHeadCommitShaAsync</c>), the outer catch in
    /// <c>AppendExternalCiIfNeededAsync</c> must rethrow it via the unconditional
    /// <c>catch (OperationCanceledException) { throw; }</c> path.
    /// This test exercises the <c>TryReadHeadShaAsync</c> call site directly — <c>CommitAndPushAsync</c>
    /// succeeds normally so the OCE originates at the expected location in the try block.
    /// </summary>
    [Fact]
    public async Task AppendExternalCi_WhenPipelineCancelledAtTryReadHeadSha_OcePropagates()
    {
        var run = CreateRun();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // CommitAllAsync and PushBranchAsync succeed (default mocks) so execution reaches
        // TryReadHeadShaAsync → GetHeadCommitShaAsync. Throwing here with the pipeline CT
        // (ct.IsCancellationRequested == true) must trigger the unconditional rethrow:
        //   catch (OperationCanceledException) { throw; }
        // and NOT the per-poll-timeout guard (when !ct.IsCancellationRequested).
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var context = BuildContext(run);

        var act = async () => await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "pipeline CT cancellation originating from TryReadHeadShaAsync must propagate out of AppendExternalCiIfNeededAsync");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head-abc");
        // GetRunStatusAsync must return non-Pending so WaitForCiRunsToAppearAsync passes through
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Running } } });
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() });
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-ci-poll",
        IssueIdentifier = "542",
        IssueTitle = "CI polling fix",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-cipoll-test-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-542-ci-poll"
    };

    private QualityGateContext BuildContext(PipelineRun run) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = 2,
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
/// Additional tests for <see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/> covering
/// the early-return guard paths: local gate failures, null PipelineProvider, and skipCiIfNoChanges.
/// </summary>
public class QualityGateExecutorGuardTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IPipelineProvider> _mockPipelineProvider = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly QualityGateExecutor _executor;

    public QualityGateExecutorGuardTests()
    {
        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        // Default: CommitAllAsync succeeds with no changes exception to exercise skipCiIfNoChanges
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-abc");
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Running, Jobs = [new() { Name = "build", State = PipelineRunState.Running }] });
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] });
    }

    [Fact]
    public async Task AppendExternalCi_WhenCompilationFailed_ReturnsReportUnchanged()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "error CS0001" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var context = BuildContext(CreateRun());
        var result = await _executor.AppendExternalCiIfNeededAsync(context, report, false, CancellationToken.None);

        result.Should().BeSameAs(report, "local gate failure should short-circuit before CI polling");
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AppendExternalCi_WhenTestsFailed_ReturnsReportUnchanged()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = false, Details = "5 tests failed" }
        };

        var context = BuildContext(CreateRun());
        var result = await _executor.AppendExternalCiIfNeededAsync(context, report, false, CancellationToken.None);

        result.Should().BeSameAs(report, "local test failure should short-circuit before CI polling");
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AppendExternalCi_WhenPipelineProviderIsNull_ReturnsReportUnchanged()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        // Context with PipelineProvider = null — use a dedicated builder to guarantee null
        var run = CreateRun();
        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = 1,
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1)
            },
            AgentProvider = new Mock<IAgentProvider>().Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            RepoProvider = _mockRepoProvider.Object,
            PipelineProvider = null, // explicitly null
            QualityGateConfigs = new List<QualityGateConfiguration>()
        };

        var result = await _executor.AppendExternalCiIfNeededAsync(context, report, false, CancellationToken.None);

        result.Should().BeSameAs(report, "null PipelineProvider should short-circuit without CI polling");
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AppendExternalCi_SkipCiIfNoChanges_WhenNoChangesToCommit_SkipsCiAndReturnsOriginalReport()
    {
        // CommitAllAsync throws "No changes to commit" — the skipCiIfNoChanges=true path should
        // emit a skip message and return the original report without appending an ExternalCi gate.
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(new InvalidOperationException("No changes to commit"));

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var context = BuildContext(CreateRun());
        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, report, allowEmptyCommit: false, CancellationToken.None, skipCiIfNoChanges: true);

        // ExternalCi gate should NOT be appended — CI was skipped
        result.ExternalCi.Should().BeNull("skip-ci-if-no-changes path should return report without ExternalCi gate");
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Verify the skip message was emitted
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.Is<string>(s => s.Contains("skipped"))), Times.Once);
    }

    [Fact]
    public async Task AppendExternalCi_SkipCiIfNoChanges_False_WhenNoChanges_StillRunsCi()
    {
        // When skipCiIfNoChanges=false but no changes, it should push an empty commit and run CI
        _mockRepoProvider.SetupSequence(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(new InvalidOperationException("No changes to commit"))  // first call: no changes
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);        // second call: empty commit

        // HasCommitsAheadAsync returns false so it doesn't take the "commits ahead" bypass path
        _mockRepoProvider.Setup(r => r.HasCommitsAheadAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var context = BuildContext(CreateRun());
        // allowEmptyCommit=true → creates empty commit and runs CI
        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, report, allowEmptyCommit: true, CancellationToken.None, skipCiIfNoChanges: false);

        result.ExternalCi.Should().NotBeNull("empty commit path should proceed to CI polling");
        result.ExternalCi!.Passed.Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => new()
    {
        RunId = "qg-guard-test",
        IssueIdentifier = "999",
        IssueTitle = "Guard test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-guard-{Guid.NewGuid():N}"),
        BranchName = "feature/guard-test"
    };

    private QualityGateContext BuildContext(PipelineRun run) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = 1,
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
/// Tests for the branch-moved cancellation re-poll behaviour introduced in issue #2271.
/// When CI is cancelled because the branch HEAD moved to a new commit, the executor
/// re-enters <c>PollCiWithNotStartedRetryAsync</c> on the new HEAD rather than treating
/// the cancellation as a gate failure.
/// </summary>
public class QualityGateExecutorBranchMovedCancellationTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    private static readonly PipelineRunStatus CancelledStatus = new()
    {
        State = PipelineRunState.Cancelled,
        Jobs = new List<PipelineJobResult>()
    };

    private static readonly PipelineRunStatus PassedStatus = new()
    {
        State = PipelineRunState.Passed,
        Jobs = new List<PipelineJobResult>()
    };

    private static readonly PipelineRunStatus RunningStatus = new()
    {
        State = PipelineRunState.Running,
        Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Running } }
    };

    private static readonly PipelineRunStatus PendingNoCiStatus = new()
    {
        State = PipelineRunState.Pending,
        Jobs = new List<PipelineJobResult>()
    };

    public QualityGateExecutorBranchMovedCancellationTests()
    {
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        // Default: CommitAllAsync and PushBranchAsync succeed
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Acceptance criterion: When CI is cancelled and the branch HEAD has moved, the executor
    /// re-polls on the new HEAD SHA and the final result is Passed — the outer retry slot is
    /// not consumed (verified by ExternalCi.Passed == true).
    /// </summary>
    [Fact]
    public async Task WhenCiCancelledAndBranchMoved_RepollsViaNotStartedLoop_DoesNotConsumeRetrySlot()
    {
        var run = CreateRun();

        // Initial commit push reads HEAD → "sha-original"
        // After Cancelled, branch-moved check reads HEAD → "sha-moved"
        _mockRepoProvider.SetupSequence(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-original")   // read after CommitAndPushAsync
            .ReturnsAsync("sha-moved");     // read inside branch-moved loop after Cancelled

        // CI appears immediately (Running → not the not-started path)
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus);

        // First poll (sha-original) → Cancelled; second poll (sha-moved) → Passed
        _mockPipelineProvider.SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledStatus)
            .ReturnsAsync(PassedStatus);

        var context = BuildContext(run);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("branch-moved re-poll reached Passed — no outer retry slot consumed");

        // TODO [WARNING]: This test does not assert that run.InfrastructureRetryCount was not
        // incremented. A future refactor that accidentally routes through ExecuteInfraRetryAsync
        // before the branch-moved loop would still produce Passed == true and pass this test.
        // Consider adding: run.InfrastructureRetryCount.Should().Be(0).

        // WaitForCompletionAsync called twice: once for sha-original (Cancelled), once for sha-moved (Passed)
        // TODO [WARNING]: The SHA parameter is matched with It.IsAny<string?>(), so a bug that
        // re-polls on sha-original twice (never using sha-moved) would still satisfy Times.Exactly(2).
        // Consider adding a verify with It.Is<string?>(s => s == "sha-moved") for the second call.
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /// <summary>
    /// Acceptance criterion: When the new HEAD SHA has no CI run yet, the not-started re-push
    /// logic fires (CommitAllAsync called), and the final result is Passed.
    /// </summary>
    [Fact]
    public async Task WhenCiCancelledAndBranchMoved_NewShaHasNoCiYet_NotStartedLoopRepushes()
    {
        var run = CreateRun();

        // Track whether the not-started re-push has happened yet; after it does,
        // GetRunStatusAsync should return Running so WaitForCiRunsToAppearAsync exits.
        var repushDone = false;

        // HEAD reads: initial push → "sha-original", branch-moved check → "sha-moved",
        // after not-started re-push inside PollCiWithNotStartedRetryAsync → "sha-repush"
        _mockRepoProvider.SetupSequence(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-original")   // after CommitAndPushAsync
            .ReturnsAsync("sha-moved")      // branch-moved check after Cancelled
            .ReturnsAsync("sha-repush");    // after not-started re-push

        // Track the re-push: when CommitAllAsync is called with "re-trigger CI" the flag is set
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.Is<string>(s => s.Contains("re-trigger CI")),
                It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((WorkspacePath _, string _, IReadOnlyList<string>? _, bool _, CancellationToken _, IReadOnlyList<string>? _) =>
            {
                repushDone = true;
                return (IReadOnlyList<string>)Array.Empty<string>();
            });
        // TODO [WARNING]: The commit message filter "re-trigger CI" also matches the infra-retry
        // path message "chore: re-trigger CI after infrastructure failure (N)", so repushDone == true
        // does not exclusively prove the not-started re-push fired. Use a more specific filter such as
        // s.Contains("not started") to unambiguously distinguish the two re-push paths.

        // GetRunStatusAsync: sha-original always Running (CI present from the start),
        // sha-moved returns Pending until the re-push fires, sha-repush always Running.
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.Is<string?>(sha => sha == "sha-original"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus);
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.Is<string?>(sha => sha == "sha-moved"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => repushDone ? RunningStatus : PendingNoCiStatus);
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.Is<string?>(sha => sha == "sha-repush"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus);

        // sha-original → Cancelled; sha-repush → Passed
        _mockPipelineProvider.SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledStatus)  // sha-original
            .ReturnsAsync(PassedStatus);    // sha-repush

        // CiNotStartedTimeout short so WaitForCiRunsToAppearAsync times out quickly for sha-moved
        var context = BuildContext(run, ciNotStartedTimeout: TimeSpan.FromMilliseconds(50));
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("not-started re-push on the new SHA triggered CI and it passed");

        // The flag proves the not-started re-push fired inside PollCiWithNotStartedRetryAsync
        repushDone.Should().BeTrue("the not-started re-push inside PollCiWithNotStartedRetryAsync must have fired");
    }

    /// <summary>
    /// Acceptance criterion: When CI is cancelled and HEAD == polled SHA, the branch-moved loop
    /// does NOT fire — the existing infra-retry path applies (or the gate fails if no infra retries).
    /// </summary>
    [Fact]
    public async Task WhenCiCancelledAndBranchNotMoved_TreatsAsInfraFailure_NoMoveLoopFires()
    {
        var run = CreateRun();

        // HEAD is always the same SHA — no branch movement
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-fixed");

        // CI appears immediately (Running)
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus);

        // Poll → Cancelled (genuine pre-emption, HEAD didn't move)
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledStatus);

        // MaxInfrastructureRetries = 0 so no infra retry loop runs; gate simply fails
        var context = BuildContext(run, maxInfraRetries: 0);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse("HEAD unchanged → genuine cancellation → gate failure");

        // WaitForCompletionAsync called exactly once — the branch-moved loop exited immediately (HEAD == pollSha)
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "no branch-moved re-poll should have fired when HEAD stayed at the same SHA");

        // No not-started re-push commits (CommitAllAsync called only for the initial push, not for re-trigger)
        _mockRepoProvider.Verify(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.Is<string>(s => s.Contains("re-trigger CI")),
                It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()),
            Times.Never,
            "branch-moved loop must not have triggered a re-push when HEAD was unchanged");
    }

    /// <summary>
    /// Acceptance criterion: Branch-moved re-polls are bounded by <c>CiCancelledMoveMaxRetries</c>.
    /// When the branch keeps moving (new SHA on every check), the loop stops after the configured
    /// maximum and returns the final Cancelled result without looping infinitely.
    /// </summary>
    [Fact]
    public async Task WhenBranchKeepsMoving_MovedRetryBoundRespected_DoesNotLoopInfinitely()
    {
        const int maxMoveRetries = 3;
        var run = CreateRun();

        // HEAD keeps advancing: sha-0 (initial push), then sha-1, sha-2, sha-3 (branch-moved checks)
        _mockRepoProvider.SetupSequence(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-0")
            .ReturnsAsync("sha-1")
            .ReturnsAsync("sha-2")
            .ReturnsAsync("sha-3");

        // Each new SHA has CI appearing immediately (Running)
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus);

        // Every poll returns Cancelled (branch keeps moving)
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledStatus);

        var context = BuildContext(run, ciCancelledMoveMaxRetries: maxMoveRetries, maxInfraRetries: 0);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse("max retries exhausted without CI passing");

        // WaitForCompletionAsync: 1 initial + maxMoveRetries branch-moved = 4 total
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(maxMoveRetries + 1),
            $"should have polled exactly {maxMoveRetries + 1} times (1 initial + {maxMoveRetries} branch-moved retries)");

        // TODO [WARNING]: This test does not cover CiCancelledMoveMaxRetries = 0 (the boundary
        // that disables the feature entirely). When the setting is 0, the while loop condition
        // branchMovedRetries < 0 is false on entry, so no branch-moved re-poll fires even when
        // HEAD has moved. A separate test case verifying Times.Once (only the initial poll) for
        // CiCancelledMoveMaxRetries = 0 would guard against an off-by-one regression.
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-branchmove",
        IssueIdentifier = "2271",
        IssueTitle = "CI branch-moved cancellation fix",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-branchmove-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2271-ci-cancelled"
    };

    private QualityGateContext BuildContext(
        PipelineRun run,
        TimeSpan? ciNotStartedTimeout = null,
        int maxInfraRetries = 2,
        int ciCancelledMoveMaxRetries = 3) => new()
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = maxInfraRetries,
                CiCancelledMoveMaxRetries = ciCancelledMoveMaxRetries,
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(10),
                CiNotStartedTimeout = ciNotStartedTimeout ?? TimeSpan.FromMinutes(5),
                CiNotStartedMaxRetries = 1,
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
/// Tests for the "CI passed on prior SHA → re-trigger skipped" fix introduced in issue #2317.
/// Before the fix, <c>PollCiWithNotStartedRetryAsync</c> only checked for workflow runs matching
/// the re-trigger commit SHA. A CI run that passed on the original SHA was invisible, causing the
/// loop to keep pushing empty commits until <c>CiNotStartedMaxRetries</c> was exhausted.
/// </summary>
public class QualityGateExecutorCiNotStartedPriorShaTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorCiNotStartedPriorShaTests()
    {
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        // Default: CommitAllAsync and PushBranchAsync succeed
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        // TODO: The 6-parameter allowEmpty overload (WorkspacePath, string, IReadOnlyList<string>?,
        // bool allowEmpty, CancellationToken, IReadOnlyList<string>?) is not set up here. If the
        // production re-trigger path calls CommitAllAsync with allowEmpty:true (6-arg form), Moq
        // will not match the 5-arg setup above, CommitAllAsync returns null by default, and the
        // Times.Never verification in WhenCiPassedOnPriorSha_SkipsReTriggerAndReportsPass may pass
        // vacuously. Add the allowEmpty overload to match QualityGateExecutorCiNotStartedExhaustionTests
        // for consistency and correctness. (#2317)
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Acceptance criterion: when CI has already passed on a prior SHA of the same branch,
    /// the pipeline detects it before re-pushing and proceeds without creating a re-trigger commit.
    /// </summary>
    [Fact]
    public async Task WhenCiPassedOnPriorSha_SkipsReTriggerAndReportsPass()
    {
        var run = CreateRun();

        // All SHA-specific queries return Pending — CI never started on the pushed commit
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.Is<string?>(s => s != null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = new List<PipelineJobResult>() });

        // Branch-wide query (SHA=null) returns Passed — CI ran on a prior SHA
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.Is<string?>(s => s == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Passed } }
            });

        var context = BuildContext(run);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Gate must pass — CI was detected via prior SHA
        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("CI passed on a prior SHA; re-trigger must be skipped");

        // No empty re-trigger commit was created (uses unique "not started" substring to exclude
        // the infra-retry path which uses "infrastructure failure")
        _mockRepoProvider.Verify(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(),
                It.Is<string>(s => s.Contains("not started")),
                It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()),
            Times.Never,
            "no re-trigger commit should be pushed when CI already passed on a prior SHA");

        // WaitForCompletionAsync must never be called — the branch-wide Passed result is returned directly
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "WaitForCompletionAsync must not be called when CI already passed on a prior SHA");

        // TODO: This test only exercises the case where the branch-wide Passed result is detected on
        // the first retry attempt (attempt 0, CiNotStartedMaxRetries=2). The key regression scenario
        // is that the loop already performed one or more re-trigger pushes (creating new SHAs), then
        // the branch-wide check detects a passing run on the original SHA on a later attempt. With the
        // current test setup, the guard would still pass even if the branch-wide check were only
        // evaluated outside the loop (before attempt 0). Add a complementary test with
        // CiNotStartedMaxRetries=3 and a mock that returns Passed on the branch-wide call only on
        // attempt 2, confirming the guard fires mid-loop. (#2317)
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-prior-sha",
        IssueIdentifier = "2317",
        IssueTitle = "CI passed on prior SHA fix",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-prior-sha-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2317-ci-not-started"
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
            // Short timeout so WaitForCiRunsToAppearAsync exits immediately
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(1),
            CiNotStartedMaxRetries = 2,
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(5),
            // Large value — must never be reached in this test
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
/// Tests for the "retries exhausted → deterministic failure" fix introduced in issue #2317.
/// Before the fix, exhausting <c>CiNotStartedMaxRetries</c> fell through to a full
/// <c>WaitForCompletionAsync</c> call on the re-trigger SHA (which also had no CI runs), blocking
/// for the entire <c>ExternalCiTimeout</c> before finally returning Pending. After the fix the
/// method returns immediately with <c>State=Failed</c> and sets <c>run.FailureReason</c>.
/// </summary>
public class QualityGateExecutorCiNotStartedExhaustionTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorCiNotStartedExhaustionTests()
    {
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        // Default: CommitAllAsync and PushBranchAsync succeed
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
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Acceptance criterion: when <c>CiNotStartedMaxRetries</c> is exhausted the run fails
    /// deterministically — <c>WaitForCompletionAsync</c> is never called, the gate fails,
    /// and <c>run.FailureReason</c> is set to the expected message.
    /// </summary>
    [Fact]
    public async Task WhenRetriesExhausted_FailsDeterministicallyWithoutWaitForCompletion()
    {
        const int maxRetries = 2;
        var run = CreateRun();

        // All GetRunStatusAsync calls (any SHA including null) return Pending — genuine outage
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = new List<PipelineJobResult>() });

        var context = BuildContext(run, ciNotStartedMaxRetries: maxRetries);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Gate must fail
        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse("CI never started — gate must fail after retries exhausted");

        // run.FailureReason must be set to the exact acceptance-criterion string
        run.FailureReason.Should().Be($"CI never started after {maxRetries} retries",
            "FailureReason must encode the retry count as required by the acceptance criterion");

        // WaitForCompletionAsync must NEVER be called — this is the key regression guard.
        // Before the fix, the exhaustion path fell through to WaitForCompletionAsync on a
        // re-trigger SHA that had no CI runs, blocking for the entire ExternalCiTimeout.
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "WaitForCompletionAsync must not be called on retry exhaustion — no CI will ever appear on the re-trigger SHA");

        // Exactly maxRetries empty re-trigger commits: one per attempt (0..maxRetries-1),
        // then the attempt >= maxRetries branch fires at attempt=maxRetries before any commit.
        // Uses "not started" substring to distinguish from the infra-retry path ("infrastructure failure").
        _mockRepoProvider.Verify(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(),
                It.Is<string>(s => s.Contains("not started")),
                It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()),
            Times.Exactly(maxRetries),
            $"expected exactly {maxRetries} re-trigger commits (one per attempt before exhaustion)");

        // TODO: The acceptance criterion requires "CompletedAt set" on retry exhaustion. CompletedAt
        // is set via run.MarkCompleted() inside PullRequestFinalizationService.FinalizePullRequest,
        // called downstream from FinalizeDraftPrAsync. This test does not assert run.CompletedAt != null
        // (or run.CompletedAtOffset != null), so a regression where MarkCompleted() is skipped (e.g.,
        // due to the pre-existing OCE gap noted in PullRequestFinalizationService.cs:148) would not be
        // caught here. Add: run.CompletedAtOffset.Should().NotBeNull("CompletedAt must be set on exhaustion").
        // Requires FinalizePullRequest mock to invoke the real finalization path or a spy to verify
        // MarkCompleted was called. (#2317)
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-exhaustion",
        IssueIdentifier = "2317",
        IssueTitle = "CI retry exhaustion fix",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-exhaustion-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2317-ci-not-started"
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
            // Short timeout so WaitForCiRunsToAppearAsync exits immediately
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(1),
            CiNotStartedMaxRetries = ciNotStartedMaxRetries,
            ExternalCiPollInterval = TimeSpan.FromMilliseconds(5),
            // Large value — must never be reached if the fix is correct
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

    /// <summary>
    /// Issue #2954: CI-never-started exhaustion must set FailureCategory = InfrastructureFailure
    /// and propagate IsInfrastructureFailure so RunRetryLoopAsync does not invoke the LLM fix agent.
    /// </summary>
    // TODO [WARNING] (TestQualityReviewer): This test uses MaxRetries = 0 (the BuildContext default for this class),
    // so RunRetryLoopAsync is never entered and the IsInfrastructureFailure short-circuit at RetryLoop.cs:366 is not
    // exercised. This duplicates the assertion from CiPollingCoordinatorTests.NotStartedExhaustion_SetsInfrastructureFailureCategory
    // without adding the missing agent-invocation check. Add a test with MaxRetries > 0 and a mock IAgentProvider
    // verified Times.Never to prove the LLM fix agent is actually skipped on exhaustion.
    [Fact]
    public async Task WhenRetriesExhausted_SetsInfrastructureFailureCategory()
    {
        const int maxRetries = 1;
        var run = CreateRun();

        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        var context = BuildContext(run, ciNotStartedMaxRetries: maxRetries);
        var result = await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();

        // FailureCategory must be InfrastructureFailure so the run is not retried with LLM
        run.FailureCategory.Should().Be(FailureReason.InfrastructureFailure,
            "CI-never-started exhaustion is infrastructure failure, not a code-level failure that LLM can fix");

        // IsInfrastructureFailure must be true on the gate result so RunRetryLoopAsync can detect it
        result.ExternalCi.IsInfrastructureFailure.Should().BeTrue(
            "IsInfrastructureFailure must propagate to GateResult to allow RunRetryLoopAsync to short-circuit");
    }
}

/// <summary>
/// Regression test for issue #3046: <c>_externalCiDuration</c> histogram must be recorded on
/// all exit paths from <c>RunExternalCiPollAsync</c>, including when
/// <c>PollAndHandleInfraRetryAsync</c> throws an unhandled provider exception.
///
/// Before the fix, the <c>_externalCiDuration.Record</c> call was placed after the await without
/// a try/finally wrapper, so any exception from <c>PollAndHandleInfraRetryAsync</c> silently
/// dropped the duration sample, biasing P99 downward.
/// </summary>
public class QualityGateExecutorExternalCiDurationTelemetryTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly MetricCollector<double> _externalCiCollector;

    private readonly Mock<IQualityGateValidator> _mockValidator = new();
    private readonly Mock<IAgentProvider> _mockAgent = new();
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IPipelineProvider> _mockPipelineProvider = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingLocalReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "ok" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" }
    };

    public QualityGateExecutorExternalCiDurationTelemetryTests()
    {
        _externalCiCollector = new MetricCollector<double>(
            _meterFactory, PipelineTelemetry.SourceName, "quality_gate.external_ci.duration");

        _run = new PipelineRun
        {
            RunId = "telemetry-ext-ci-duration-3046",
            IssueIdentifier = "3046",
            IssueTitle = "ExternalCiDuration exception-path telemetry test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-extci-dur-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-3046-test"
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object,
            _meterFactory);

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        _externalCiCollector.Dispose();
        _meterFactory.Dispose();
    }

    /// <summary>
    /// Regression test for issue #3046: when <c>PollAndHandleInfraRetryAsync</c> throws an
    /// unhandled provider exception, <c>_externalCiDuration.Record</c> MUST still be called.
    ///
    /// Before the fix, the histogram received zero measurements on the exception path.
    /// After the fix (wrapping the await in try/finally), it receives exactly one measurement.
    ///
    /// The exception propagates from <c>WaitForCompletionAsync</c> through
    /// <c>PollAndHandleInfraRetryAsync</c> → <c>RunExternalCiPollAsync</c>, then is caught by
    /// <c>AppendExternalCiIfNeededAsync</c>'s outer <c>catch (Exception ex)</c> block which
    /// produces a <c>BuildCiErrorGateResult</c>. The test receives a normal (non-throwing)
    /// return value from <c>AppendExternalCiIfNeededAsync</c>.
    ///
    /// <c>GetRunStatusAsync</c> must return Running so that
    /// <c>PollCiWithNotStartedRetryAsync</c> (called inside <c>PollAndHandleInfraRetryAsync</c>)
    /// proceeds past the not-started check and reaches <c>WaitForCompletionAsync</c>, which then
    /// throws the provider exception we want to exercise.
    /// </summary>
    [Fact]
    public async Task RunExternalCiPollAsync_WhenPollAndHandleThrows_StillRecordsExternalCiDuration()
    {
        // Arrange: CI appears to be running (so we reach WaitForCompletionAsync)
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        // WaitForCompletionAsync throws a provider exception — the unhandled exception path
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider communication error"));

        // Act: AppendExternalCiIfNeededAsync absorbs the exception via catch (Exception ex)
        var result = await _executor.AppendExternalCiIfNeededAsync(
            BuildContext(), PassingLocalReport, allowEmptyCommit: false, CancellationToken.None);

        // Assert: ExternalCi gate is set (confirms the exception path was taken, not a short-circuit)
        result.ExternalCi.Should().NotBeNull(
            "the exception path must produce a CI error gate result, not null");
        result.ExternalCi!.Passed.Should().BeFalse(
            "a provider exception must result in a failed CI gate");

        // Assert: _externalCiDuration was recorded despite the exception (regression guard for #3046)
        var measurements = _externalCiCollector.GetMeasurementSnapshot();
        measurements.Should().HaveCount(1,
            "quality_gate.external_ci.duration must be recorded exactly once even when " +
            "PollAndHandleInfraRetryAsync throws (issue #3046: Record must be in a finally block)");
        // TODO [WARNING]: This assertion is too weak — BeGreaterThanOrEqualTo(0) is always satisfied
        // by Stopwatch.Elapsed.TotalSeconds (which is never negative). Even a zeroed or unused stopwatch
        // would pass. Consider tightening to BeGreaterThan(0) once the async operations consistently
        // produce a measurable elapsed time (the test involves async mocks, so sub-millisecond completion
        // is possible in theory, but in practice some positive time will have elapsed).
        measurements[0].Value.Should().BeGreaterThanOrEqualTo(0,
            "recorded duration must be a non-negative elapsed time");
    }

    // TODO [WARNING]: Only the exception path through RunExternalCiPollAsync is tested here.
    // The fix (wrapping the await in try/finally) affects both success and exception exit paths,
    // but only the exception path is exercised by the test above. A complementary test verifying
    // that _externalCiDuration is also recorded on the normal-return path would make coverage
    // symmetric and guard against a regression where the finally is replaced by two separate
    // Record calls (one in try, one in catch). The success path was presumably covered by
    // pre-existing tests, but a dedicated test here would make the regression guard explicit.

    // ── Helpers ──────────────────────────────────────────────────────────────

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
            .ReturnsAsync("sha-3046-abc");

        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
    }

    private QualityGateContext BuildContext() => new()
    {
        Run = _run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
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
        QualityGateConfigs = new List<QualityGateConfiguration>()
    };
}
