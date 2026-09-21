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
/// Verifies that WaitForPostPrCiAsync guards the <c>pipeline.step.duration</c> and
/// <c>pipeline.step.count</c> telemetry calls against genuine pipeline cancellation
/// (ct.IsCancellationRequested). A mid-poll cancellation must NOT emit a partial elapsed-time
/// sample that would distort p50/p99 histogram aggregations.
///
/// Regression tests for issue #2794: before the fix, the outer <c>finally</c> block in
/// WaitForPostPrCiAsync emitted the step-duration observation even when the pipeline was
/// cancelled mid-poll, injecting a near-zero outlier into the histogram.
/// </summary>
public class QualityGateExecutorWaitForPostPrCiTelemetryGuardTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly MetricCollector<double> _stepDurationCollector;
    private readonly MetricCollector<long> _stepCountCollector;

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

    public QualityGateExecutorWaitForPostPrCiTelemetryGuardTests()
    {
        _stepDurationCollector = new MetricCollector<double>(
            _meterFactory, PipelineTelemetry.SourceName, "pipeline.step.duration");
        _stepCountCollector = new MetricCollector<long>(
            _meterFactory, PipelineTelemetry.SourceName, "pipeline.step.count");

        _run = new PipelineRun
        {
            RunId = "telemetry-guard-test",
            IssueIdentifier = "2794",
            IssueTitle = "WaitForPostPrCi telemetry guard test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-telguard-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-2794-test"
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
        _stepDurationCollector.Dispose();
        _stepCountCollector.Dispose();
        _meterFactory.Dispose();
    }

    // ── Cancellation guard ─────────────────────────────────────────────────────

    /// <summary>
    /// Regression test for issue #2794: when the pipeline cancellation token is cancelled
    /// while WaitForPostPrCiAsync is polling CI, neither <c>pipeline.step.duration</c> nor
    /// <c>pipeline.step.count</c> must emit a <c>step_name="WaitForPostPrCi"</c> observation.
    ///
    /// Before the fix, the outer <c>finally</c> block emitted unconditionally, recording a
    /// partial (near-zero) elapsed time that distorted p50/p99 histogram aggregations for
    /// multi-hour CI waits.
    ///
    /// The test triggers WaitForPostPrCiAsync by routing through the skipCiIfNoChanges path
    /// (cleanup commit throws "no changes"), which causes FinalizePullRequest to be called and
    /// then WaitForPostPrCiAsync to start.  WaitForCompletionAsync cancels the outer
    /// CancellationToken before returning, simulating a pipeline cancel mid-poll.
    /// </summary>
    [Fact]
    public async Task WhenCancelledDuringCiWait_StepDurationAndStepCountAreNotEmitted()
    {
        // Arrange: local gates pass so FinalizePullRequest fires and WaitForPostPrCiAsync starts.
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        using var cts = new CancellationTokenSource();

        // GetRunStatusAsync: CI is running (so WaitForCompletionAsync is actually called)
        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        // WaitForCompletionAsync: cancel the outer token and then throw OCE.
        // This simulates pipeline cancellation arriving while WaitForPostPrCiAsync is
        // blocked inside PollAndHandleInfraRetryAsync.
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string? _, TimeSpan _, CancellationToken _) =>
            {
                await cts.CancelAsync();
                cts.Token.ThrowIfCancellationRequested();
                return (PipelineRunStatus)null!; // unreachable
            });

        // Act: run with the cancellation token that will be cancelled during the CI wait
        await _executor.ProceedToQualityGatesAsync(BuildContext(), cts.Token);

        // Assert: no WaitForPostPrCi observation in pipeline.step.duration
        var stepDurationMeasurements = _stepDurationCollector.GetMeasurementSnapshot();
        stepDurationMeasurements
            .Where(m => m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "WaitForPostPrCi")))
            .Should().BeEmpty(
                "pipeline.step.duration must NOT emit a WaitForPostPrCi sample when the pipeline " +
                "cancellation token fires mid-poll (issue #2794)");

        // Assert: no WaitForPostPrCi observation in pipeline.step.count
        var stepCountMeasurements = _stepCountCollector.GetMeasurementSnapshot();
        stepCountMeasurements
            .Where(m => m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "WaitForPostPrCi")))
            .Should().BeEmpty(
                "pipeline.step.count must NOT emit a WaitForPostPrCi sample when the pipeline " +
                "cancellation token fires mid-poll (issue #2794)");
    }

    /// <summary>
    /// Positive control: when WaitForPostPrCiAsync completes normally (CI passes),
    /// the step-duration and step-count observations MUST be emitted.  This verifies
    /// the guard does not suppress telemetry on the success path.
    /// </summary>
    [Fact]
    public async Task WhenCiCompletesNormally_StepDurationAndStepCountAreEmitted()
    {
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // pipeline.step.duration must have a WaitForPostPrCi observation
        _stepDurationCollector.GetMeasurementSnapshot()
            .Should().Contain(m => m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "WaitForPostPrCi")),
                "pipeline.step.duration must be recorded for WaitForPostPrCi on the success path");

        // pipeline.step.count must have a WaitForPostPrCi observation with value 1
        _stepCountCollector.GetMeasurementSnapshot()
            .Should().Contain(m =>
                m.Value == 1 &&
                m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "WaitForPostPrCi")),
                "pipeline.step.count must be recorded for WaitForPostPrCi on the success path");
    }

    /// <summary>
    /// When WaitForPostPrCiAsync encounters an inner CI timeout (the ExternalCiTimeout fires,
    /// which is NOT the outer pipeline cancellation token), the step-duration and step-count
    /// observations MUST still be emitted.  Only genuine pipeline cancellation suppresses them.
    /// </summary>
    [Fact]
    public async Task WhenInnerCiTimeoutFires_StepDurationAndStepCountAreEmitted()
    {
        // Use the same "no changes on cleanup commit" path as WhenCiCompletesNormally so that
        // WaitForPostPrCiAsync is reached after exactly one pre-PR WaitForCompletionAsync call.
        SetupValidatorAlwaysPasses();
        SetupNoChangesToCommit();

        _mockPipelineProvider
            .Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        // Use a call counter to distinguish the two WaitForCompletionAsync calls:
        //   call 1 — initial QG pre-PR CI → Passed
        //   call 2 — WaitForPostPrCiAsync → inner OCE (ExternalCiTimeout fired, outer ct NOT cancelled)
        // The outer CancellationToken passed to ProceedToQualityGatesAsync is CancellationToken.None,
        // so ct.IsCancellationRequested = false in WaitForPostPrCiAsync's finally guard.
        using var innerCts = new CancellationTokenSource();
        await innerCts.CancelAsync(); // already-cancelled token to simulate expired inner timeout

        var waitCallCount = 0;
        _mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            // TODO [WARNING]: ReturnsAsync(Func<T>) invokes the delegate synchronously to obtain the
            // return value. A synchronous throw from inside the factory lambda is NOT wrapped in a
            // faulted Task — Moq may propagate it from the mock call site itself or substitute a
            // default (null) return value, meaning the second WaitForCompletionAsync call may never
            // actually throw OperationCanceledException to the awaiting production code. This makes
            // the inner-timeout code path untested: if the exception is swallowed, the assertions
            // pass vacuously and a regression that removes the guard would not be caught.
            // Fix: use .Returns(Task.FromException<PipelineRunStatus>(new OperationCanceledException(...)))
            // or a SetupSequence with .ThrowsAsync() on the second call.
            // See review findings: Correctness WARNING and DotNetSpecialist WARNING — tests line ~232,
            // and TestQualityReviewer WARNING — tests line ~232
            .ReturnsAsync(() =>
            {
                waitCallCount++;
                if (waitCallCount == 1)
                    return new PipelineRunStatus
                    {
                        State = PipelineRunState.Passed,
                        Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
                    };
                // Call 2 (post-PR CI): simulate inner timeout — throw OCE with inner token,
                // outer CancellationToken.None remains un-cancelled.
                throw new OperationCanceledException(innerCts.Token);
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        // Inner timeout is NOT a pipeline cancellation — telemetry must still be emitted.
        // The guard in WaitForPostPrCiAsync's finally is `if (!ct.IsCancellationRequested)`.
        // Since the outer ct is CancellationToken.None, this evaluates to true → Record/Add fire.
        _stepDurationCollector.GetMeasurementSnapshot()
            .Should().Contain(m => m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "WaitForPostPrCi")),
                "pipeline.step.duration must be recorded for WaitForPostPrCi on the inner-timeout path " +
                "(inner ExternalCiTimeout fires, outer pipeline ct remains un-cancelled)");

        _stepCountCollector.GetMeasurementSnapshot()
            .Should().Contain(m =>
                m.Value == 1 &&
                m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "WaitForPostPrCi")),
                "pipeline.step.count must be recorded for WaitForPostPrCi on the inner-timeout path");
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
            .ReturnsAsync("sha-guard-test-abc");

        _mockCallbacks.Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(
                It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        _mockIssueOps.Setup(o => o.SwapLabelAsync(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Cleanup done"],
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
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" }
            });
    }

    /// <summary>
    /// Simulates the skipCiIfNoChanges path: the initial QG pass commits successfully
    /// (so pre-PR CI runs), but the cleanup-pass commit throws "No changes" →
    /// skipCiIfNoChanges fires → FinalizePullRequest is called → WaitForPostPrCiAsync starts.
    /// </summary>
    private void SetupNoChangesToCommit()
    {
        _mockRepoProvider.SetupSequence(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>)    // initial QG pass — commits ok
            .ThrowsAsync(new InvalidOperationException("No changes to commit")); // cleanup pass — skip CI
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
            Identifier = "2794",
            Title = "WaitForPostPrCi telemetry guard test",
            Description = "Test description",
            Labels = new[] { "bug" }
        }
    };
}
