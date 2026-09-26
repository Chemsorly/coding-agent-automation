using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Direct characterization tests for <see cref="CiPollingCoordinator"/> phase methods.
/// These tests exercise the coordinator via <see cref="QualityGateExecutor"/> (which constructs
/// the coordinator internally), verifying the three CI-polling paths that were extracted from
/// <c>QualityGateExecutor.ExternalCi.cs</c> in issue #2889.
///
/// Gaps covered:
/// 1. Not-started retry exhaustion — FailureReason is set and Failed status returned
/// 2. Branch-wide already-running CI guard — waits instead of re-pushing
/// 3. MaxInfrastructureRetries exact boundary — exactly N retries before exit
/// 4. InfrastructureRetryCount isolation in post-PR CI path
/// 5. Last-minute race avoidance guard before re-push
///
/// TODO [WARNING]: The ConflictRestart path in PollCiWithNotStartedRetryAsync is not covered by
/// any of these tests. This path is one of the two early-exit branches in both
/// PollCiWithNotStartedRetryAsync (mergeability check → CheckForMergeConflictAsync) and
/// HandleBranchMovedRetryLoopAsync. The issue prerequisites require pinning CI-polling paths
/// before refactoring; a silent regression (e.g. moved/skipped conflict check) would not be
/// caught. Consider adding a Gap 6 test: stub IsPullRequestBehindBaseAsync to return Conflicted
/// and verify that AppendExternalCiIfNeededAsync returns a ConflictRestart report without
/// pushing an empty commit.
/// </summary>
public class CiPollingCoordinatorTests
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

    public CiPollingCoordinatorTests()
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

    // ── Gap 1: PollCiWithNotStartedRetryAsync — not-started retry exhaustion ──

    /// <summary>
    /// When CI never starts after CiNotStartedMaxRetries empty-commit pushes,
    /// PollCiWithNotStartedRetryAsync should:
    ///   - Return a Failed status (not block on WaitForCompletionAsync)
    ///   - Set run.FailureReason to "CI never started after N retries"
    ///   - Push exactly CiNotStartedMaxRetries empty commits (one per retry attempt, not on exhaustion)
    /// </summary>
    [Fact]
    public async Task WhenCiNeverStarts_AfterMaxRetries_SetFailureReasonAndReturnsFailed()
    {
        const int maxRetries = 2;
        var run = CreateRun();
        // No PR number — skip mergeability check
        run.PullRequestNumber = null;

        var context = BuildContext(run, ciNotStartedMaxRetries: maxRetries);

        // GetRunStatusAsync always returns Pending (CI never starts)
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Pending,
                Jobs = new List<PipelineJobResult>()
            });

        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Must return failed gate — not a timeout or error
        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();
        result.ExternalCi.GateName.Should().Be("External CI");

        // FailureReason must be set
        run.FailureReason.Should().NotBeNullOrEmpty();
        run.FailureReason.Should().Contain("CI never started after");
        run.FailureReason.Should().Contain(maxRetries.ToString());

        // WaitForCompletionAsync must NOT have been called — deterministic failure path
        _mockPipelineProvider.Verify(
            p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "must not call WaitForCompletionAsync when CI never started");

        // Exactly maxRetries empty commits pushed (one per non-final retry attempt)
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.Exactly(maxRetries),
            "must push one empty commit per retry attempt (not on the exhaustion pass)");
    }

    // ── Gap 2: PollCiWithNotStartedRetryAsync — already-running branch guard ──

    /// <summary>
    /// When CI is already running on a prior SHA of the branch (detected in the branch-wide check),
    /// PollCiWithNotStartedRetryAsync should wait for completion instead of pushing a re-trigger
    /// commit. No additional empty commit should be pushed.
    /// </summary>
    [Fact]
    public async Task WhenCiAlreadyRunningOnBranch_WaitsForCompletionWithoutRepush()
    {
        var run = CreateRun();
        run.PullRequestNumber = null;

        var context = BuildContext(run, ciNotStartedMaxRetries: 1);

        // SHA-specific check always returns Pending (no runs for our SHA yet)
        // Branch-wide check returns Running (CI running on a prior SHA)
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? sha, CancellationToken _) =>
                sha == null
                    ? new PipelineRunStatus
                    {
                        State = PipelineRunState.Running,
                        Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
                    }
                    : new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
            });

        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("CI was running and then passed");

        // WaitForCompletionAsync must be called with null SHA (branch-wide wait)
        _mockPipelineProvider.Verify(
            p => p.WaitForCompletionAsync(
                run.BranchName!, null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "must wait on null-SHA (branch-wide) when CI already running on a prior SHA");

        // No empty re-trigger commit should be pushed
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.Never,
            "must NOT push a re-trigger commit when CI is already running");
    }

    // ── Gap 3: PollAndHandleInfraRetryAsync — MaxInfrastructureRetries exact boundary ──

    /// <summary>
    /// Verifies that the infrastructure-retry loop stops after exactly MaxInfrastructureRetries
    /// retries. With MaxInfrastructureRetries = 2, a run that always fails with an infra error
    /// must see exactly 3 WaitForCompletionAsync calls: initial poll + 2 retries.
    /// </summary>
    [Fact]
    public async Task InfraRetryLoop_StopsAfterExactlyMaxInfrastructureRetries()
    {
        const int maxInfraRetries = 2;
        var run = CreateRun();
        run.PullRequestNumber = null;

        var context = BuildContext(run, maxInfraRetries: maxInfraRetries);

        // GetRunStatusAsync returns non-Pending so WaitForCiRunsToAppearAsync passes through
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        // All WaitForCompletionAsync calls return infrastructure failure
        var infraFailure = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = [new()
            {
                Name = "build",
                State = PipelineRunState.Failed,
                LogContent = "lost communication with the server"
            }]
        };
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(infraFailure);

        // Empty commits for infra retries
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);

        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();

        // 1 initial call + exactly maxInfraRetries retry calls
        // TODO [WARNING]: This mock stubs all WaitForCompletionAsync calls uniformly to infraFailure,
        // so the initial poll and every retry are indistinguishable. If the coordinator ever uses a
        // different call path (e.g. different overload or SHA argument) for the initial poll vs. a retry
        // poll, the Times.Exactly count would silently remain the same while the implementation diverged.
        // Consider making the initial call and retry calls distinguishable (e.g. via SHA argument
        // matching) and asserting failure details that can only come from the infra-retry classification.
        _mockPipelineProvider.Verify(
            p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(1 + maxInfraRetries),
            $"must call WaitForCompletionAsync exactly {1 + maxInfraRetries} times (1 initial + {maxInfraRetries} retries)");

        run.InfrastructureRetryCount.Should().Be(maxInfraRetries);
    }

    // ── Gap 4: WaitForPostPrCiAsync — InfrastructureRetryCount isolation ──

    /// <summary>
    /// Verifies that WaitForPostPrCiAsync resets InfrastructureRetryCount to 0 before polling
    /// (so post-PR CI gets its own budget) and restores the prior count afterward (so the run
    /// summary reflects the total infra retries across both pre-PR and post-PR CI polls).
    /// </summary>
    [Fact]
    public async Task WaitForPostPrCi_ResetsAndRestoresInfrastructureRetryCount()
    {
        const int priorInfraRetries = 2;
        const int maxInfraRetries = 3;

        var mockValidator = new Mock<IQualityGateValidator>();
        var mockAgent = new Mock<IAgentProvider>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();

        mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "ok" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" }
            });

        var executor = new QualityGateExecutor(
            mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            mockHistoryService.Object);

        // Commit sequence: first succeeds (pre-PR CI), second throws no-changes (cleanup skip)
        // TODO [WARNING]: This test uses SetupSequence on CommitAllAsync to steer the orchestrator
        // through the pre-PR CI → cleanup → post-PR CI pipeline path. If the orchestrator adds a new
        // CommitAllAsync call before the post-PR CI phase (e.g. a new pipeline step), the sequence
        // misaligns: the "no-changes" exception fires at the wrong point and the test either fails for
        // the wrong reason or passes vacuously. Additionally, if PipelineProvider is null or BranchName
        // is empty, WaitForPostPrCiAsync returns early leaving InfrastructureRetryCount unchanged —
        // the assertion would then pass without the post-PR CI path having executed. To make this test
        // more robust, add explicit assertions that WaitForCompletionAsync was called at least twice
        // (once pre-PR, once post-PR) to confirm the post-PR CI path ran.
        _mockRepoProvider.SetupSequence(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>)
            .ThrowsAsync(new InvalidOperationException("No changes to commit"));

        // GetRunStatusAsync returns Running so WaitForCiRunsToAppearAsync passes through
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        // All CI calls pass
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
            });

        SetupDefaultCallbackMocks();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        mockAgent.Setup(a => a.GetHealthStatus()).Returns(new AgentHealthStatus { IsExecuting = false });
        mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["done"],
                Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
            });

        var run = new PipelineRun
        {
            RunId = "infra-count-isolation-test",
            IssueIdentifier = "2889",
            IssueTitle = "InfraRetryCount isolation",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-infracount-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-2889-infracount-test",
            InfrastructureRetryCount = priorInfraRetries  // simulate pre-PR CI already consumed 2 retries
        };

        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = maxInfraRetries,
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50),
                StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1)
            },
            AgentProvider = mockAgent.Object,
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
                Identifier = "2889",
                Title = "InfraRetryCount isolation",
                Description = "test",
                Labels = []
            }
        };

        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // After post-PR CI completes, InfrastructureRetryCount must be restored to the prior value
        // (priorInfraRetries + 0 post-PR retries = priorInfraRetries, since CI passed).
        run.InfrastructureRetryCount.Should().Be(priorInfraRetries,
            "WaitForPostPrCiAsync must restore the prior InfrastructureRetryCount after post-PR CI completes");
    }

    // ── Gap 5: PollCiWithNotStartedRetryAsync — last-minute race guard ──

    /// <summary>
    /// Verifies the last-minute race-avoidance guard: when GetRunStatusAsync (SHA-specific)
    /// returns a non-Pending state just before an empty-commit push, WaitForCompletionAsync
    /// is called immediately without pushing an empty commit.
    /// </summary>
    [Fact]
    public async Task WhenCiAppearsJustBeforeRepush_SkipsEmptyCommitAndWaitsForCompletion()
    {
        var run = CreateRun();
        run.PullRequestNumber = null;

        var context = BuildContext(run, ciNotStartedMaxRetries: 1);

        // WaitForCiRunsToAppearAsync uses GetRunStatusAsync with non-null SHA → Pending (no runs yet)
        // The last-check guard also calls GetRunStatusAsync with the specific SHA → Running (just appeared)
        // Branch-wide check (SHA=null) is never reached when SHA-specific returns non-Pending
        // TODO [WARNING]: This test uses a shared callCount to distinguish the WaitForCiRunsToAppearAsync
        // poll from the last-check guard call. With CiNotStartedTimeout = 50ms and ExternalCiPollInterval
        // = 50ms, the polling loop may fire more than once before the timeout expires, causing callCount
        // to reach 2 inside WaitForCiRunsToAppearAsync rather than at the last-check guard. The test
        // would then pass by testing "CI appeared during the appearance-wait" rather than the documented
        // "CI appeared just before re-push" path. To make the path deterministic, set CiNotStartedTimeout
        // to ~1ms and ExternalCiPollInterval to a larger value (e.g. 500ms) so the appearance-wait exits
        // after exactly one poll and the second GetRunStatusAsync call is unambiguously the last-check guard.
        var callCount = 0;
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? sha, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref callCount);
                // First call from WaitForCiRunsToAppearAsync → Pending (CI not started)
                // Second call (last-check guard before re-push) → Running (race caught)
                if (sha != null && call >= 2)
                    return new PipelineRunStatus
                    {
                        State = PipelineRunState.Running,
                        Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
                    };

                return new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] };
            });

        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
            });

        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("CI was caught by the last-check race guard and then passed");

        // No empty re-trigger commit should be pushed when the race is caught
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.Never,
            "must NOT push a re-trigger commit when the last-check guard catches CI appearing");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
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
        _mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head-test");
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupDefaultCallbackMocks()
    {
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(
                It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = "coordinator-test",
        IssueIdentifier = "2889",
        IssueTitle = "CiPollingCoordinator tests",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-coord-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-2889-coordinator-test"
    };

    private QualityGateContext BuildContext(
        PipelineRun run,
        int ciNotStartedMaxRetries = 1,
        int maxInfraRetries = 0) => new()
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = maxInfraRetries,
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                CiNotStartedMaxRetries = ciNotStartedMaxRetries,
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50),
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

    // ── Issue #2954: PR state checks in CI polling loop ───────────────────────

    /// <summary>
    /// When PR is merged mid-loop, CheckPullRequestStillOpenAsync returns PrMerged.
    /// No further CommitAllAsync (empty re-trigger commits) or PushBranchAsync should be called
    /// after detection. The initial commit of actual changes (before CI polling) is expected.
    /// </summary>
    // TODO [WARNING] (TestQualityReviewer): This test validates the first-iteration exit path, not a true mid-loop
    // scenario. GetPullRequestStateAsync always returns Merged, so detection fires on attempt 0 before any empty
    // commit is pushed. The actual mid-loop scenario — where N-1 iterations push empty commits and iteration N
    // detects the merge — is untested. Add a test where GetPullRequestStateAsync returns Open on the first N-1
    // calls and Merged on call N, verifying no further commits are pushed after detection.
    [Fact]
    public async Task PrMergedMidLoop_NoFurtherCommitOrPush_TerminatesSucceeded()
    {
        var run = CreateRun();
        run.PullRequestNumber = "42";

        var context = BuildContext(run, ciNotStartedMaxRetries: 2);

        // CI never starts
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR state: returns Merged on first call
        _mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Merged);

        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Must set terminal state
        run.CurrentStep.Should().Be(PipelineStep.PrMerged);
        run.FinalLabel.Should().BeNull("merged PR run has no error label");

        // No empty re-trigger commit (allowEmpty: true) should be pushed after PR merge detection
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.Never,
            "must NOT push an empty re-trigger commit after detecting a merged PR");
    }

    /// <summary>
    /// When PR is closed mid-loop, run should terminate as Cancelled.
    /// </summary>
    [Fact]
    public async Task PrClosedMidLoop_TerminatesCancelled()
    {
        var run = CreateRun();
        run.PullRequestNumber = "43";

        var context = BuildContext(run, ciNotStartedMaxRetries: 2);

        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        _mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(43, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Closed);

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.PrClosed);
        run.FinalLabel.Should().Be(AgentLabels.Cancelled);
    }

    /// <summary>
    /// When PR state returns Open, the existing re-push behavior is unchanged.
    /// </summary>
    [Fact]
    public async Task PrOpen_CiNeverStarts_StillPushesEmptyCommit()
    {
        var run = CreateRun();
        run.PullRequestNumber = "44";

        var context = BuildContext(run, ciNotStartedMaxRetries: 1);

        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR is still open
        _mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(44, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Open);

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Empty commit must still be pushed when PR is open
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            // TODO [WARNING] (TestQualityReviewer): Times.AtLeastOnce is weaker than needed. With ciNotStartedMaxRetries: 1
            // the loop runs attempt 0 (push) then attempt 1 (exhaustion exit) — only one empty commit should be pushed.
            // Using Times.AtLeastOnce means a regression pushing multiple commits per iteration would not be caught.
            // Consider using Times.Once here.
            Times.AtLeastOnce,
            "must push empty commit when PR is still open and CI never started");
    }

    /// <summary>
    /// When PullRequestNumber is null but LinkedPullRequest.Number = 42, both
    /// CheckForMergeConflictAsync and CheckPullRequestStillOpenAsync must use number 42.
    /// </summary>
    [Fact]
    public async Task PrNumberResolvedFromLinkedPullRequest_WhenPullRequestNumberIsNull()
    {
        var run = CreateRun();
        run.PullRequestNumber = null;
        run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 42,
            BranchName = "feature/auto-42-linked",
            Url = "https://github.com/org/repo/pull/42",
            IsDraft = false
        };

        var context = BuildContext(run, ciNotStartedMaxRetries: 1);

        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR state returns Merged to confirm the call was made with number 42
        _mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Merged);

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // GetPullRequestStateAsync must be called with 42 (resolved from LinkedPullRequest.Number)
        _mockRepoProvider.Verify(
            r => r.GetPullRequestStateAsync(42, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "GetPullRequestStateAsync must be called with number from LinkedPullRequest when PullRequestNumber is null");

        // IsPullRequestBehindBaseAsync (conflict check) must also be called with 42
        // TODO [WARNING] (TestQualityReviewer): Times.AtLeastOnce is sufficient to confirm the number is used,
        // but does not rule out additional calls with a different number. The mock setup already constrains the
        // call to number 42 (GetPullRequestStateAsync returns Merged, terminating the loop on the first iteration
        // where the PR-state check fires), so in practice only one call is made. Consider using Times.Once to pin
        // the expected call count and catch regressions where the conflict check is called with a wrong number.
        _mockRepoProvider.Verify(
            r => r.IsPullRequestBehindBaseAsync(42, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "IsPullRequestBehindBaseAsync must also use the LinkedPullRequest number");
    }

    /// <summary>
    /// When GetPullRequestStateAsync throws, the loop should continue (fail-open) and push
    /// an empty commit as if the PR were still open.
    /// </summary>
    [Fact]
    public async Task PrStateQueryThrows_FailOpen_LoopContinues()
    {
        var run = CreateRun();
        run.PullRequestNumber = "45";

        var context = BuildContext(run, ciNotStartedMaxRetries: 1);

        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // Simulate a transient provider error
        _mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(45, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset by peer"));

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Loop must continue and push empty commit — fail-open behavior
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.AtLeastOnce,
            "must still push empty commit when PR state query throws (fail-open)");
    }

    /// <summary>
    /// CI never started exhaustion: FailureCategory must be InfrastructureFailure
    /// and IsInfrastructureFailure must be true on the returned gate result.
    /// </summary>
    // TODO [WARNING] (TestQualityReviewer): This test uses MaxRetries = 0 (the BuildContext default), so RunRetryLoopAsync
    // is never entered and the IsInfrastructureFailure short-circuit guard in RetryLoop.cs:366 is not exercised.
    // This only verifies the flag is *set*, not that the LLM fix agent is *skipped*. See companion test
    // NotStartedExhaustion_WithMaxRetriesEnabled_DoesNotInvokeRetryAgent below, which goes through
    // ProceedToQualityGatesAsync with MaxRetries = 1 and verifies the agent is never called.
    [Fact]
    public async Task NotStartedExhaustion_SetsInfrastructureFailureCategory()
    {
        const int maxRetries = 1;
        var run = CreateRun();
        run.PullRequestNumber = null;

        var context = BuildContext(run, ciNotStartedMaxRetries: maxRetries);

        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        var result = await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();

        // The failure category must be InfrastructureFailure
        run.FailureCategory.Should().Be(FailureReason.InfrastructureFailure,
            "CI-never-started exhaustion is an infrastructure failure, not a code-level failure");

        // The gate result must carry the IsInfrastructureFailure flag
        result.ExternalCi.IsInfrastructureFailure.Should().BeTrue(
            "IsInfrastructureFailure must propagate to GateResult so RunRetryLoopAsync can short-circuit");
    }

    /// <summary>
    /// Issue #2954 acceptance criterion: "CI never started exhaustion never triggers an LLM fix attempt."
    /// Goes through the full <see cref="QualityGateExecutor.ProceedToQualityGatesAsync"/> path with
    /// <c>MaxRetries = 1</c> so <c>RunRetryLoopAsync</c> is entered. Verifies the
    /// <c>IsInfrastructureFailure</c> guard breaks the loop before <c>IAgentProvider.ExecuteAsync</c>
    /// is called with a retry-fix prompt.
    /// </summary>
    [Fact]
    public async Task NotStartedExhaustion_WithMaxRetriesEnabled_DoesNotInvokeRetryAgent()
    {
        // Use a dedicated validator + agent mock so we can assert on ExecuteAsync precisely.
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockAgent = new Mock<IAgentProvider>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();

        var run = CreateRun();
        run.PullRequestNumber = null;

        // Validator returns a passing compilation/tests report — CI polling is what fails.
        mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(PassingReport);

        // CI never starts.
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // Agent returns a valid result for any call (e.g. the feedback-collection call).
        mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["{}"],
                Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
            });

        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        SetupDefaultCallbackMocks();
        _mockCallbacks.Setup(c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        var executor = new QualityGateExecutor(
            mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            mockHistoryService.Object);

        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                // MaxRetries = 1 ensures RunRetryLoopAsync is entered and its guard is exercised.
                MaxRetries = 1,
                MaxInfrastructureRetries = 0,
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                CiNotStartedMaxRetries = 1,
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50),
                StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1),
                TransientRetryDelay = TimeSpan.Zero
            },
            AgentProvider = mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            RepoProvider = _mockRepoProvider.Object,
            PipelineProvider = _mockPipelineProvider.Object,
            QualityGateConfigs = new List<QualityGateConfiguration>()
        };

        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // run.RetryCount must be 0: it is only incremented inside RunFixAgentIterationAsync, which
        // must never be reached because the IsInfrastructureFailure guard breaks the loop first.
        run.RetryCount.Should().Be(0,
            "IsInfrastructureFailure guard must break the loop before RunFixAgentIterationAsync is called");

        // No prompt containing 'Quality gates failed' (the retry-fix prompt signature) must be
        // dispatched. The feedback-collection prompt contains 'Pipeline Failure Feedback' — its
        // possible call is excluded from this assertion so the test is not sensitive to that path.
        mockAgent.Verify(
            a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Quality gates failed")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Never,
            "the retry-fix agent must never be invoked for a CI-never-started infrastructure failure");
    }

    /// <summary>
    /// True mid-loop scenario: N-1 CI-not-started iterations push empty re-trigger commits,
    /// then on iteration N the PR state check returns Merged. No further commits must be pushed
    /// after detection. This test supplements PrMergedMidLoop_NoFurtherCommitOrPush_TerminatesSucceeded
    /// (which only validates first-iteration detection) by exercising the actual "already pushed
    /// N-1 commits, then detected" path.
    /// </summary>
    [Fact]
    public async Task PrMergedAfterNIterations_StopsAfterNMinus1Pushes()
    {
        // With ciNotStartedMaxRetries: 3, the loop runs attempts 0, 1, 2.
        // GetPullRequestStateAsync returns Open on attempt 0 and 1, Merged on attempt 2.
        // Expected: exactly 2 empty re-trigger commits pushed (attempts 0 and 1),
        // then the merge is detected on attempt 2 and no further commit is pushed.
        const int ciNotStartedMaxRetries = 3;
        var run = CreateRun();
        run.PullRequestNumber = "99";

        var context = BuildContext(run, ciNotStartedMaxRetries: ciNotStartedMaxRetries);

        // CI never starts on any SHA.
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR state: Open → Open → Merged (detected on the third call).
        _mockRepoProvider.SetupSequence(r => r.GetPullRequestStateAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Open)
            .ReturnsAsync(PullRequestState.Open)
            .ReturnsAsync(PullRequestState.Merged);

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Terminal state must be PrMerged.
        run.CurrentStep.Should().Be(PipelineStep.PrMerged,
            "run must terminate as PrMerged when the PR is detected as merged mid-loop");
        run.FinalLabel.Should().BeNull("merged run ends Succeeded, not agent:error or agent:cancelled");

        // Exactly 2 empty re-trigger commits: one pushed on attempt 0, one on attempt 1.
        // Attempt 2 detects the merge before pushing, so no third commit is pushed.
        _mockRepoProvider.Verify(
            r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.Exactly(2),
            "must push exactly N-1 empty commits before detecting the merge on iteration N");
    }
}
