using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Regression test for: cancellation during quality gates is rethrown without finalizing the run.
/// The token is already cancelled by then, so a transition or label swap sent from the executor
/// would be dropped or would mislabel a SIGTERM drain. The host and AgentJobRunner decide instead:
/// a retry on SIGTERM, a Cancelled outcome on operator cancel. A run that is already Failed or
/// Cancelled is left as it is.
/// </summary>
public class QualityGateCancellationLabelTests
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

    public QualityGateCancellationLabelTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-cancel",
            IssueIdentifier = "241",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-cancel-test-{Guid.NewGuid():N}")
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
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_RethrowsWithoutSwappingLabels()
    {
        // Arrange: validator throws OperationCanceledException (simulating cancellation during quality gates)
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        var act = () => _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: the cancellation propagates to the host
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Assert: no label is swapped here; the label comes from the host's Cancelled outcome
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCallbacks.Verify(
            c => c.RemoveAllAgentLabels(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_DoesNotTransitionToCancelled()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        var act = () => _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_LeavesCompletedAtUnset()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        var act = () => _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _run.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledViaLinkedCts_Rethrows()
    {
        // Arrange: simulate orchestrator-level cancellation via linked CTS
        using var orchestratorCts = new CancellationTokenSource();

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<PipelineRunEventReport>?>()))
            .Returns(async (WorkspacePath _, IReadOnlyList<QualityGateConfiguration> _, CancellationToken ct, Action<PipelineRunEventReport>? _) =>
            {
                // Cancel while "validating"
                await orchestratorCts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = true },
                    Tests = new GateResult { GateName = "Tests", Passed = true }
                }; // unreachable
            });

        var context = BuildContext(orchestratorCts);

        // Act
        var act = () => _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: the cancellation propagates and no label is swapped here
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenAlreadyCancelled_DoesNotSwapLabelAgain()
    {
        // Arrange: run is already in Cancelled state (e.g., cancelled by a prior step)
        _run.CurrentStep = PipelineStep.Cancelled;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: SwapAgentLabel is NOT called (guard condition prevents double-labeling)
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: TransitionTo(Cancelled) is NOT called again
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenAlreadyFailed_DoesNotRethrowCancellation()
    {
        // Arrange: run is already in Failed state (e.g., failed by a prior step)
        _run.CurrentStep = PipelineStep.Failed;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        var act = () => _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: the guard returns normally and leaves the terminal step alone
        await act.Should().NotThrowAsync();
        _run.CurrentStep.Should().Be(PipelineStep.Failed);

        // Assert: the validator threw, and nothing finalized the already-failed run.
        // TransitionTo is a mock here, so it never changes CurrentStep: only the mock's calls prove it.
        _mockValidator.Verify(v => v.ValidateAsync(
            It.IsAny<WorkspacePath>(),
            It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _run.CompletedAt.Should().BeNull();
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_DoesNotEmitCancelledOutputLine()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var emittedLines = new List<string>();
        _mockCallbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => emittedLines.Add(line));

        var context = BuildContext();

        // Act
        var act = () => _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: LocalPipelineExecutor emits the cancelled line after the host maps the exception
        await act.Should().ThrowAsync<OperationCanceledException>();
        emittedLines.Should().NotContain("🚫 Pipeline cancelled",
            "the executor rethrows and leaves the cancelled output line to LocalPipelineExecutor");
    }

    private QualityGateContext BuildContext(CancellationTokenSource? orchestratorCts = null)
    {
        return new QualityGateContext
        {
            Run = _run,
            Config = _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            RepoProvider = _mockRepoProvider.Object,
            OrchestratorCts = orchestratorCts,
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
