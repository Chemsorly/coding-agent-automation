using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentPhaseExecutor.FailPhaseAsync"/> (private helper).
/// Tested indirectly via <see cref="AgentPhaseExecutor.ExecuteCodeGenerationAsync"/>
/// by triggering a timeout exit code from the agent provider.
/// Validates: Requirements 27.3
/// </summary>
public class FailPhaseAsyncTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly AgentPhaseExecutor _orchestrator;

    public FailPhaseAsyncTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-fail",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = "/tmp/workspace"
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _orchestrator = new AgentPhaseExecutor(_mockLogger.Object);

        // Default health status so stall monitor doesn't interfere
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = DateTime.UtcNow
            });

        // Default: SwapLabelAsync completes successfully
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: UpdateFileChangeStats completes successfully
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task FailPhase_WhenAgentReturnsTimeoutExitCode_SetsFailureReasonAndCompletedAt()
    {
        // Arrange: agent returns timeout exit code
        var agentResult = new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        var context = BuildContext();

        // Act
        var result = await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync sets FailureReason
        result.Should().BeFalse();
        _run.FailureReason.Should().NotBeNullOrEmpty();
        _run.FailureReason.Should().Contain("timed out");
    }

    [Fact]
    public async Task FailPhase_WhenAgentReturnsTimeoutExitCode_SetsCompletedAt()
    {
        // Arrange
        var beforeTest = DateTime.UtcNow;
        var agentResult = new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        var context = BuildContext();

        // Act
        await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync sets CompletedAt
        _run.CompletedAt.Should().NotBeNull();
        _run.CompletedAt!.Value.Should().BeOnOrAfter(beforeTest);
    }

    [Fact]
    public async Task FailPhase_WhenAgentReturnsTimeoutExitCode_CallsSwapLabelAsyncWithErrorLabel()
    {
        // Arrange
        var agentResult = new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        var context = BuildContext();

        // Act
        await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync calls SwapLabelAsync with the error label
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FailPhase_WhenAgentReturnsTimeoutExitCode_CallsTransitionToFailed()
    {
        // Arrange
        var agentResult = new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        var context = BuildContext();

        // Act
        await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync calls TransitionTo(PipelineStep.Failed)
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
    }

    [Fact]
    public async Task FailPhase_WhenAgentReturnsTimeoutExitCode_CallsAddRunToHistory()
    {
        // Arrange
        var agentResult = new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        var context = BuildContext();

        // Act
        await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync calls AddRunToHistory
        _mockCallbacks.Verify(c => c.AddRunToHistory(_run), Times.Once);
    }

    [Fact]
    public async Task FailPhase_WhenAgentReturnsTimeoutExitCode_ReturnsFalse()
    {
        // Arrange
        var agentResult = new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        var context = BuildContext();

        // Act
        var result = await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync returns false (pipeline should not continue)
        result.Should().BeFalse();
    }

    [Fact]
    public async Task FailPhase_WhenOperationCancelledAndNotOrchestratorCts_SetsAllFieldsCorrectly()
    {
        // Arrange: agent throws OperationCanceledException (simulating timeout via exception path)
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException("Agent timed out"));

        var context = BuildContext();

        // Act
        var result = await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync is called via the OperationCanceledException catch block
        result.Should().BeFalse();
        _run.FailureReason.Should().NotBeNullOrEmpty();
        _run.FailureReason.Should().Contain("timed out");
        _run.CompletedAt.Should().NotBeNull();
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
        _mockCallbacks.Verify(c => c.AddRunToHistory(_run), Times.Once);
    }

    [Fact]
    public async Task FailPhase_WhenGenericExceptionAndNoFileChanges_FailsFast()
    {
        // Arrange: agent throws IOException with no file changes
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new IOException("disk full"));

        var context = BuildContext();

        // Act
        var result = await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: FailPhaseAsync is called — run fails fast with no quality gate entry
        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("disk full");
        _run.FailureReason.Should().Contain("no file changes");
        _run.CompletedAt.Should().NotBeNull();
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(_run.IssueIdentifier, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
        _mockCallbacks.Verify(c => c.AddRunToHistory(_run), Times.Once);
    }

    [Fact]
    public async Task FailPhase_WhenGenericExceptionWithPartialFileChanges_ContinuesToQualityGates()
    {
        // Arrange: agent throws IOException but there ARE file changes
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new IOException("connection reset"));

        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Callback<PipelineRun>(r => r.FilesChangedCount = 3)
            .Returns(Task.CompletedTask);

        var context = BuildContext();

        // Act
        var result = await _orchestrator.ExecuteCodeGenerationAsync(context, CancellationToken.None);

        // Assert: continues to quality gates (returns true), no FailPhaseAsync side effects
        result.Should().BeTrue();
        _run.FailureReason.Should().BeNull();
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(It.IsAny<string>(), AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
    }

    private AgentPhaseContext BuildContext()
    {
        return new AgentPhaseContext
        {
            Run = _run,
            Config = _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            OrchestratorCts = null,
            Issue = new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue",
                Description = "Test description",
                Labels = new[] { "bug" }
            },
            ParsedIssue = new ParsedIssue
            {
                RequirementsSection = "Test requirements",
                AcceptanceCriteria = new[] { "AC1", "AC2" }
            }
        };
    }
}
