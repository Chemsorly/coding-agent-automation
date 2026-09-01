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
