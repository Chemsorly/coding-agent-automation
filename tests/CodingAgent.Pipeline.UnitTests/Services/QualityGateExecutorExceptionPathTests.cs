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

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenExceptionThrown_AndRunAlreadyFailed_StillCallsFinalizeOnce()
    {
        // Arrange: pre-set CurrentStep to Failed (simulating an inner call that set Failed and then threw)
        // The exception arm has no guard — it runs unconditionally regardless of CurrentStep.
        // This test documents that pre-existing behavior so it cannot silently regress.
        _run.CurrentStep = PipelineStep.Failed;

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

        // Assert: the exception arm has no guard, so finalization runs exactly once
        // (AddRunToHistoryAsync and FailureReason are each set once by the catch arm)
        _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(_run), Times.Once);
        _run.FailureReason.Should().NotBeNull();
        // TODO: [WARNING] The assertion above is weaker than intended. It verifies AddRunToHistoryAsync
        // and FailureReason, but does not assert that TransitionTo(PipelineStep.Failed) was called
        // Times.Once or that _run.CompletedAt is non-null (i.e., MarkCompleted() was called). A future
        // guard that skips finalization when CurrentStep is already Failed would cause MarkCompleted()
        // and TransitionTo to be skipped, but this test would still pass. Add:
        //   _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
        //   _run.CompletedAt.Should().NotBeNull();
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
