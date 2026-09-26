using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Regression tests for: unhandled exception in ProceedToQualityGatesAsync must call
/// run.MarkCompleted() so that CompletedAt is non-null and the run is not treated as
/// a ghost row by ReconcileOrphanedPipelineRunsAsync.
///
/// Mirrors QualityGateCancellationLabelTests for the catch(OperationCanceledException) path.
/// </summary>
public class QualityGateExecutorExceptionPathTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly QualityGateExecutor _orchestrator;

    public QualityGateExecutorExceptionPathTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-exception",
            IssueIdentifier = "241",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-exception-test-{Guid.NewGuid():N}")
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 3,
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _orchestrator = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        // Default: callbacks complete successfully
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        // Default: issue ops complete successfully
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_SetsCompletedAt()
    {
        // Arrange: validator throws a generic exception (simulating an unexpected failure)
        var beforeTest = DateTime.UtcNow;
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: CompletedAt must be set — run must not be a ghost row
        _run.CompletedAt.Should().NotBeNull();
        _run.CompletedAt!.Value.Should().BeOnOrAfter(beforeTest);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_TransitionsToFailed()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
        // TODO: [WARNING] Missing negative assertion: this test does not verify that
        // TransitionTo(PipelineStep.Cancelled) was never called. An InvalidOperationException
        // mis-routed to the cancellation path (e.g., if exception-type filtering were accidentally
        // reordered) would still pass this test because Times.Once on Failed does not imply
        // Cancelled was never called. Add:
        //   _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Never);
        // TODO: Times.Once on PipelineStep.Failed does not verify that TransitionTo(PipelineStep.RunningQualityGates)
        // was also called (unconditionally at the top of ProceedToQualityGatesAsync before the try block). A future
        // refactor that moves the initial transition inside the try block and then lets the exception suppress it
        // would not be caught by this assertion. Consider adding:
        //   _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.RunningQualityGates), Times.Once);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_SwapsToErrorLabel()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: agent:error label is swapped (not agent:cancelled)
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once);
        // TODO: No negative assertion verifies that AgentLabels.Cancelled is NOT swapped on this path (contrast with
        // OCE path reference tests). The test would pass even if both Error and Cancelled labels were swapped.
        // Consider adding:
        //   _mockIssueOps.Verify(o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Cancelled, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_SetsFailureReason()
    {
        // Arrange
        const string exceptionMessage = "unexpected error";
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException(exceptionMessage));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: FailureReason includes the exception message
        _run.FailureReason.Should().NotBeNull();
        _run.FailureReason.Should().Contain(exceptionMessage);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_CallsAddRunToHistoryAsync()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: AddRunToHistoryAsync must be called exactly once on the exception path
        _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(_run), Times.Once);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_EmitsFailureOutputLine()
    {
        // Arrange
        const string exceptionMessage = "unexpected error";
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException(exceptionMessage));

        var emittedLines = new List<string>();
        _mockCallbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => emittedLines.Add(line));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: an output line containing the failure message must have been emitted
        emittedLines.Should().Contain(line => line.Contains(exceptionMessage),
            "the exception arm must emit an output line containing the failure reason");
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_MarkCompletedBeforeTransitionTo()
    {
        // Arrange: capture the value of CompletedAt at the moment TransitionTo(Failed) is called
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        DateTime? completedAtAtTransitionTime = null;
        _mockCallbacks.Setup(c => c.TransitionTo(PipelineStep.Failed))
            .Callback(() => completedAtAtTransitionTime = _run.CompletedAt);

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: MarkCompleted() must have been called before TransitionTo(Failed)
        completedAtAtTransitionTime.Should().NotBeNull(
            "run.MarkCompleted() must be called before TransitionTo(PipelineStep.Failed)");
    }

    // TODO: [WARNING] These two tests (AndRunAlreadyFailed / AndRunAlreadyCancelled) are structurally
    // identical and differ only in the CurrentStep value. Consider merging them into a single
    // [Theory] / [InlineData] parameterized test to remove duplication and ensure any future fix
    // (e.g. the logger overload correction below) is applied in one place rather than two.
    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_AndRunAlreadyFailed_DoesNotCallFinalizeRunAsync()
    {
        // Arrange: pre-set CurrentStep to Failed — simulates an inner call (e.g. RunRetryLoopAsync)
        // that already finalized the run and then re-threw. The guard added to the catch arm must
        // short-circuit before touching FailureReason or calling FinalizeRunAsync.
        _run.CurrentStep = PipelineStep.Failed;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: guard fired — FinalizeRunAsync side effects must not have run
        _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(_run), Times.Never);
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _run.FailureReason.Should().BeNull("guard must return before setting FailureReason");
        _run.CompletedAt.Should().BeNull("guard must return before calling MarkCompleted()");
        // Guard fires before _logger.Error — no error-level log entry should have been emitted.
        // Use the 3-argument generic overload Error<T>(Exception, string, T) to match the production
        // call _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId).
        _mockLogger.Verify(l => l.Error(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object>()),
            Times.Never);
    }

    /// <summary>
    /// Regression test for issue #3045: the <c>catch(Exception)</c> guard in
    /// <c>ProceedToQualityGatesAsync</c> was broadened from
    /// <c>PipelineStep.Failed or PipelineStep.Cancelled</c> to <c>run.CurrentStep.IsTerminal()</c>,
    /// which now includes <c>PipelineStep.ConflictRestart</c>. This test verifies that the expanded
    /// guard short-circuits <c>FinalizeRunAsync</c> when <c>CurrentStep</c> is already
    /// <c>ConflictRestart</c> at catch-arm entry.
    ///
    /// NOTE: In practice, after the pre-retry guard fix also introduced in #3045, there is no
    /// realistic production code path where <c>ConflictRestart</c> is set AND a non-OCE exception
    /// subsequently propagates to this catch arm — the pre-retry guard returns immediately when
    /// <c>ConflictRestart</c> is detected, so no further code runs that could throw. This test is
    /// therefore a structural guard test (verifying the IsTerminal() expansion covers ConflictRestart)
    /// rather than a scenario test. It mirrors the pattern used by
    /// <c>AndRunAlreadyFailed</c> and <c>AndRunAlreadyCancelled</c>, which have the same structure:
    /// pre-set the step, make the validator throw, and assert no FinalizeRunAsync side effects run.
    /// </summary>
    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_AndRunAlreadyConflictRestart_DoesNotCallFinalizeRunAsync()
    {
        // Arrange: pre-set CurrentStep to ConflictRestart to simulate any prior inner call having
        // set this terminal state. The IsTerminal() guard in the catch arm must short-circuit before
        // touching FailureReason or calling FinalizeRunAsync, preserving the ConflictRestart outcome.
        _run.CurrentStep = PipelineStep.ConflictRestart;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: IsTerminal() guard fired — FinalizeRunAsync side effects must not have run.
        // ConflictRestart outcome (agent:next label) must be preserved, not overwritten with agent:error.
        _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(_run), Times.Never);
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _run.FailureReason.Should().BeNull("guard must return before setting FailureReason");
        _run.CompletedAt.Should().BeNull("guard must return before calling MarkCompleted()");
        // TODO: [WARNING] FinalLabel is not asserted here. The issue requires "run.FinalLabel = AgentLabels.Next
        // MUST be preserved in both cases". Since this test pre-sets CurrentStep manually (without going
        // through BuildConflictRestartReport), FinalLabel is never set to AgentLabels.Next — neither as a
        // precondition nor by the production code path. The test therefore cannot detect a regression where
        // the catch arm overwrites FinalLabel. To fully cover this requirement, either: (a) pre-set
        // _run.FinalLabel = AgentLabels.Next before the act, then assert it is still AgentLabels.Next after;
        // or (b) rely on the reproduction test in QualityGateExecutorConflictRestartPreRetryTests, which
        // correctly exercises BuildConflictRestartReport and asserts run.FinalLabel == AgentLabels.Next.
        // Guard fires before _logger.Error — no error-level log entry should have been emitted.
        // Use the 3-argument generic overload Error<T>(Exception, string, T) to match the production
        // call _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId).
        _mockLogger.Verify(l => l.Error(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_AndRunAlreadyCancelled_DoesNotCallFinalizeRunAsync()
    {
        // Arrange: pre-set CurrentStep to Cancelled — simulates an inner call that cancelled the run
        // and then threw a non-OCE exception. The guard must prevent FinalizeRunAsync from overwriting
        // the Cancelled state with an Error label.
        _run.CurrentStep = PipelineStep.Cancelled;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: guard fired — FinalizeRunAsync side effects must not have run
        _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(_run), Times.Never);
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _run.FailureReason.Should().BeNull("guard must return before setting FailureReason");
        _run.CompletedAt.Should().BeNull("guard must return before calling MarkCompleted()");
        // Guard fires before _logger.Error — no error-level log entry should have been emitted.
        // Use the 3-argument generic overload Error<T>(Exception, string, T) to match the production
        // call _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId).
        _mockLogger.Verify(l => l.Error(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object>()),
            Times.Never);
    }

    private QualityGateContext BuildContext()
    {
        return new QualityGateContext
        {
            Run = _run,
            Config = _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            RepoProvider = _mockRepoProvider.Object,
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
            }
        };
    }
}
