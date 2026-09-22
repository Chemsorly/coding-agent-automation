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
        // TODO: AddRunToHistoryAsync is called in the catch(Exception) handler but is not explicitly set up here.
        // Moq's loose default (MockBehavior.Loose) returns Task.CompletedTask for Task-returning methods, so this is
        // currently safe. If the mock behaviour is ever tightened to MockBehavior.Strict, or if AddRunToHistoryAsync
        // is changed to return a ValueTask or other non-Task awaitable, all four tests will fail with a MockException
        // rather than exercising the target assertion. Add an explicit setup here if that happens:
        //   _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
        //       .Returns(Task.CompletedTask);

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
        // TODO: This test does not verify that MarkCompleted() was called *before* TransitionTo(PipelineStep.Failed)
        // and before AddRunToHistoryAsync. The issue's suggested fix requires that ordering. Consider adding a
        // Moq Callback on _mockCallbacks.TransitionTo to capture _run.CompletedAt at invocation time and assert
        // it was already non-null at that moment (or use MockSequence / InSequence ordering verification).
        // TODO: No test verifies that AddRunToHistoryAsync is actually called on the exception path. If
        // AddRunToHistoryAsync were accidentally removed from the catch block, no test would detect the regression.
        // Add a _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(...), Times.Once) here or in a separate test.
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

    // TODO: Missing edge-case test analogous to QualityGateCancellationLabelTests.ProceedToQualityGatesAsync_WhenAlreadyCancelled_DoesNotSwapLabelAgain.
    // The catch(Exception) block has no guard checking run.CurrentStep, so if run.CurrentStep is already PipelineStep.Failed
    // on entry (set by an earlier inner call), the block may double-call AddRunToHistoryAsync or double-set FailureReason.
    // Add a test that pre-sets run.CurrentStep = PipelineStep.Failed before the exception fires and verifies
    // that AddRunToHistoryAsync is called exactly once and FailureReason is set exactly once.
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
