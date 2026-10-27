using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Moq;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Pure unit tests for <see cref="QualityGateExecutor.ClassifyCiResult"/>.
/// Exercises all discriminating input combinations in isolation, without requiring a full
/// <see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/> round-trip.
/// </summary>
public class QualityGateExecutorCiTerminalStateClassifierTests
{
    // ── State-based outcomes ──────────────────────────────────────────────────

    [Fact]
    public void ConflictRestartState_ReturnsConflictRestart()
    {
        var status = MakeStatus(PipelineRunState.ConflictRestart);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.ConflictRestart);
    }

    [Fact]
    public void PrMergedState_ReturnsPrMerged()
    {
        var status = MakeStatus(PipelineRunState.PrMerged);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.PrMerged);
    }

    [Fact]
    public void PrClosedState_ReturnsPrClosed()
    {
        var status = MakeStatus(PipelineRunState.PrClosed);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.PrClosed);
    }

    // ── IsInfrastructureFailure flag ──────────────────────────────────────────

    [Fact]
    public void IsInfrastructureFailureFlag_WithNonTerminalState_ReturnsInfrastructureFailure()
    {
        // State is ordinary Failed (not a sentinel state) but the infra flag is set —
        // e.g. CI never started exhaustion classified by CiPollingCoordinator.
        var status = MakeStatus(PipelineRunState.Failed, isInfrastructureFailure: true);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.InfrastructureFailure);
    }

    [Fact]
    public void NormalResult_NonTerminalState_NoInfraFlag_ReturnsNormalResult()
    {
        var status = MakeStatus(PipelineRunState.Passed);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.NormalResult);
    }

    [Fact]
    public void FailedState_NoInfraFlag_ReturnsNormalResult()
    {
        // A plain CI failure (tests failing, not infra) must reach the normal gate-building path.
        var status = MakeStatus(PipelineRunState.Failed);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.NormalResult);
    }

    // ── Priority ordering: State checks must precede IsInfrastructureFailure ──

    [Fact]
    public void PrMergedStateWithInfrastructureFailureFlag_ReturnsPrMerged()
    {
        // A PrMerged status that also has IsInfrastructureFailure = true must route to PrMerged,
        // not InfrastructureFailure. State checks take priority. A regression swapping check order
        // would fail here.
        var status = MakeStatus(PipelineRunState.PrMerged, isInfrastructureFailure: true);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.PrMerged,
            "state-equality checks must precede the IsInfrastructureFailure check");
    }

    [Fact]
    public void PrClosedStateWithInfrastructureFailureFlag_ReturnsPrClosed()
    {
        var status = MakeStatus(PipelineRunState.PrClosed, isInfrastructureFailure: true);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.PrClosed,
            "state-equality checks must precede the IsInfrastructureFailure check");
    }

    [Fact]
    public void ConflictRestartStateWithInfrastructureFailureFlag_ReturnsConflictRestart()
    {
        var status = MakeStatus(PipelineRunState.ConflictRestart, isInfrastructureFailure: true);

        var outcome = QualityGateExecutor.ClassifyCiResult(status);

        outcome.Should().Be(CiTerminalOutcome.ConflictRestart,
            "state-equality checks must precede the IsInfrastructureFailure check");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static PipelineRunStatus MakeStatus(
        PipelineRunState state, bool isInfrastructureFailure = false) =>
        new() { State = state, Jobs = [], IsInfrastructureFailure = isInfrastructureFailure };
}

/// <summary>
/// Integration-style tests for <see cref="QualityGateExecutor.RunInfraRetryPollingAsync"/>.
/// These tests exercise the retry loop in isolation by calling it directly (it is <c>internal</c>)
/// and arranging <c>_mockPipelineProvider.WaitForCompletionAsync</c> to succeed or throw. They do
/// not exercise the full <see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/> flow.
/// </summary>
public class QualityGateExecutorRunInfraRetryPollingTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _executor;

    public QualityGateExecutorRunInfraRetryPollingTests()
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
    public async Task WhenFirstPollSucceeds_ReturnsCiResultWithExhaustedFalse()
    {
        // Default mock: WaitForCompletionAsync returns Passed on first call.
        var run = CreateRun();
        var context = BuildContext(run, maxInfrastructureRetries: 2);

        var result = await _executor.RunInfraRetryPollingAsync(context, CancellationToken.None);

        result.CiResult.Should().NotBeNull("first poll succeeded — CiResult must be populated");
        result.ExhaustedRetries.Should().BeFalse("no retries were consumed");
        result.LastException.Should().BeNull("no exception was thrown");
        run.InfrastructureRetryCount.Should().Be(0, "no infra retries were needed");
    }

    [Fact]
    public async Task WhenPollThrowsOnceThenSucceeds_ReturnsCiResultAndIncrementsInfraRetryCount()
    {
        var run = CreateRun();
        _mockPipelineProvider.SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("transient error"))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] });

        var context = BuildContext(run, maxInfrastructureRetries: 2);

        var result = await _executor.RunInfraRetryPollingAsync(context, CancellationToken.None);

        result.CiResult.Should().NotBeNull("second poll succeeded — CiResult must be populated");
        result.ExhaustedRetries.Should().BeFalse("retries were not exhausted");
        run.InfrastructureRetryCount.Should().Be(1, "one infra retry was consumed");
    }

    [Fact]
    public async Task WhenPollAlwaysThrowsAndRetriesExhausted_ReturnsExhaustedTrueWithLastException()
    {
        var run = CreateRun();
        var thrownException = new HttpRequestException("persistent outage");
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrownException);

        var context = BuildContext(run, maxInfrastructureRetries: 2);

        var result = await _executor.RunInfraRetryPollingAsync(context, CancellationToken.None);

        result.CiResult.Should().BeNull("all polls threw — no CI result");
        result.ExhaustedRetries.Should().BeTrue("all MaxInfrastructureRetries were consumed");
        result.LastException.Should().Be(thrownException);
        run.InfrastructureRetryCount.Should().Be(2,
            "each of the two re-polls (MaxInfrastructureRetries=2) must increment InfrastructureRetryCount once");
    }

    [Fact]
    public async Task WhenMaxInfrastructureRetriesIsZeroAndPollThrows_ReturnsCiResultNullExhaustedFalse()
    {
        // MaxInfrastructureRetries = 0: the loop breaks immediately on first throw without
        // incrementing localRetry, so ExhaustedRetries = false. The caller (AppendExternalCiIfNeededAsync)
        // re-throws LastException in this case.
        var run = CreateRun();
        var thrownException = new HttpRequestException("server error");
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrownException);

        var context = BuildContext(run, maxInfrastructureRetries: 0);

        var result = await _executor.RunInfraRetryPollingAsync(context, CancellationToken.None);

        result.CiResult.Should().BeNull("poll threw — no CI result");
        result.ExhaustedRetries.Should().BeFalse("MaxInfrastructureRetries=0: no retries attempted, caller re-throws");
        result.LastException.Should().Be(thrownException);
        run.InfrastructureRetryCount.Should().Be(0, "no retries were attempted");
    }

    [Fact]
    public async Task WhenOcePropagates_ThrowsWithoutReturning()
    {
        // OperationCanceledException must always propagate unconditionally from the while loop.
        var run = CreateRun();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var context = BuildContext(run, maxInfrastructureRetries: 2);

        var act = async () => await _executor.RunInfraRetryPollingAsync(context, CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "OCE must propagate unconditionally regardless of retry budget");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-test-abc");
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Running } }
            });
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] });
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-infra-retry",
        IssueIdentifier = "3958",
        IssueTitle = "Cognitive complexity refactor",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-infra-retry-test-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-3958-infra-retry"
    };

    private QualityGateContext BuildContext(PipelineRun run, int maxInfrastructureRetries) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = maxInfrastructureRetries,
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            ExternalCiPollInterval = TimeSpan.Zero,
            CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
            // CiNotStartedMaxRetries = 0: prevents WaitForCiRunsToAppearAsync from entering the
            // 16-iteration not-started retry path when GetRunStatusAsync returns an unexpected state.
            // Mirrors the pattern in QualityGateExecutorCiGateBuilderTests to isolate tests to the
            // API-connectivity-failure path that RunInfraRetryPollingAsync is intended to exercise.
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
}
