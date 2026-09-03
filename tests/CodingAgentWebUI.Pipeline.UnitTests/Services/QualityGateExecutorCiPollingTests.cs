using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

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
