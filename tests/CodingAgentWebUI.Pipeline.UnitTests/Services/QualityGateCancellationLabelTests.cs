using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Regression test for: cancellation during quality gates must set agent:cancelled label.
/// Bug: QualityGateOrchestrator.RetryLoop previously called RemoveAllAgentLabels (which sends
/// an empty string to GitHub → 422 error) instead of SwapAgentLabel with AgentLabels.Cancelled.
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
    private readonly QualityGateOrchestrator _orchestrator;

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
            WorkspacePath = "/tmp/workspace"
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 3,
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _orchestrator = new QualityGateOrchestrator(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            _mockLogger.Object);

        // Default: callbacks complete successfully
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: issue ops complete successfully
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_SwapsToAgentCancelledLabel()
    {
        // Arrange: validator throws OperationCanceledException (simulating cancellation during quality gates)
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: SwapAgentLabel is called with agent:cancelled (NOT RemoveAllAgentLabels)
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(_run.IssueIdentifier, AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: RemoveAllAgentLabels is NOT called (this was the bug)
        _mockCallbacks.Verify(
            c => c.RemoveAllAgentLabels(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_TransitionsToCancelled()
    {
        // Arrange
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Once);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledDuringValidation_SetsCompletedAt()
    {
        // Arrange
        var beforeTest = DateTime.UtcNow;
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert
        _run.CompletedAt.Should().NotBeNull();
        _run.CompletedAt!.Value.Should().BeOnOrAfter(beforeTest);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenCancelledViaLinkedCts_SwapsToAgentCancelledLabel()
    {
        // Arrange: simulate orchestrator-level cancellation via linked CTS
        using var orchestratorCts = new CancellationTokenSource();

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, IReadOnlyList<QualityGateConfiguration> _, CancellationToken ct) =>
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
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: agent:cancelled label is set
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(_run.IssueIdentifier, AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: RemoveAllAgentLabels is NOT called
        _mockCallbacks.Verify(
            c => c.RemoveAllAgentLabels(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProceedToQualityGatesAsync_WhenAlreadyCancelled_DoesNotSwapLabelAgain()
    {
        // Arrange: run is already in Cancelled state (e.g., cancelled by a prior step)
        _run.CurrentStep = PipelineStep.Cancelled;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: SwapAgentLabel is NOT called (guard condition prevents double-labeling)
        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(It.IsAny<string>(), AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: TransitionTo(Cancelled) is NOT called again
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Never);
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
